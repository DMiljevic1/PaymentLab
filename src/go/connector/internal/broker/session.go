package broker

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

// Handler processes one delivery and must ack or nack it.
type Handler func(ctx context.Context, d amqp.Delivery, pub *Publisher)

// Publisher publishes with publisher confirms on a dedicated channel.
type Publisher struct {
	ch *amqp.Channel
}

// Publish waits until the broker confirmed the message (or ctx expires).
// It runs in a producer span whose context is injected into the message headers.
func (p *Publisher) Publish(ctx context.Context, exchange, key string, msg amqp.Publishing) (err error) {
	ctx, span := tracer.Start(ctx, exchange+" publish",
		trace.WithSpanKind(trace.SpanKindProducer),
		trace.WithAttributes(
			attribute.String("messaging.system", "rabbitmq"),
			attribute.String("messaging.operation.type", "send"),
			attribute.String("messaging.destination.name", exchange),
			attribute.String("messaging.rabbitmq.destination.routing_key", key),
			attribute.String("messaging.message.id", msg.MessageId),
		))
	started := time.Now()
	defer func() {
		result := "confirmed"
		if err != nil {
			result = "failed"
			span.RecordError(err)
			span.SetStatus(codes.Error, err.Error())
		}
		attrs := metric.WithAttributes(attribute.String("exchange", exchange), attribute.String("result", result))
		publishes.Add(ctx, 1, attrs)
		publishDuration.Record(ctx, time.Since(started).Seconds(), attrs)
		span.End()
	}()

	if msg.Headers == nil {
		msg.Headers = amqp.Table{}
	}
	otel.GetTextMapPropagator().Inject(ctx, HeaderCarrier(msg.Headers))

	dc, err := p.ch.PublishWithDeferredConfirmWithContext(ctx, exchange, key, false, false, msg)
	if err != nil {
		return err
	}
	ok, err := dc.WaitContext(ctx)
	if err != nil {
		return err
	}
	if !ok {
		return errors.New("broker nacked the publish")
	}
	return nil
}

type Consumer struct {
	Queue   string
	Tag     string
	Handler Handler
}

type Session struct {
	URL        string
	Prefetch   int
	RetryDelay time.Duration
	Consumers  []Consumer
	Log        *slog.Logger
	OnState    func(connected bool)
}

// Run connects, consumes until the connection drops, and reconnects with backoff until ctx is cancelled.
// amqp091-go has no automatic reconnect, so this loop is the recovery mechanism.
func (s *Session) Run(ctx context.Context, drainTimeout time.Duration) {
	backoff := time.Second
	for ctx.Err() == nil {
		err := s.runOnce(ctx, drainTimeout)
		s.OnState(false)
		if ctx.Err() != nil {
			return
		}
		s.Log.Warn("amqp session ended, reconnecting", "error", err, "backoff", backoff.String())
		reconnects.Add(ctx, 1)
		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, 30*time.Second)
	}
}

func (s *Session) runOnce(ctx context.Context, drainTimeout time.Duration) error {
	conn, err := amqp.DialConfig(s.URL, amqp.Config{
		Heartbeat:  10 * time.Second,
		Properties: amqp.Table{"connection_name": "connector"},
	})
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer conn.Close()

	pubCh, err := conn.Channel()
	if err != nil {
		return fmt.Errorf("publish channel: %w", err)
	}
	if err := pubCh.Confirm(false); err != nil {
		return fmt.Errorf("confirm mode: %w", err)
	}
	pub := &Publisher{ch: pubCh}

	consCh, err := conn.Channel()
	if err != nil {
		return fmt.Errorf("consume channel: %w", err)
	}
	if err := DeclareTopology(consCh, s.RetryDelay); err != nil {
		return fmt.Errorf("topology: %w", err)
	}
	// prefetch applies per consumer: at most Prefetch unacked deliveries per queue.
	if err := consCh.Qos(s.Prefetch, 0, false); err != nil {
		return fmt.Errorf("qos: %w", err)
	}

	connClosed := conn.NotifyClose(make(chan *amqp.Error, 1))
	var wg sync.WaitGroup
	handlerCtx, cancelHandlers := context.WithCancel(context.Background())
	defer cancelHandlers()

	// One goroutine per delivery; prefetch bounds how many are in flight per queue.
	var dispatchers sync.WaitGroup
	for _, c := range s.Consumers {
		deliveries, err := consCh.Consume(c.Queue, c.Tag, false, false, false, false, nil)
		if err != nil {
			return fmt.Errorf("consume %s: %w", c.Queue, err)
		}
		dispatchers.Add(1)
		go func(c Consumer) {
			defer dispatchers.Done()
			queueAttr := metric.WithAttributes(attribute.String("queue", c.Queue))
			for d := range deliveries { // ends when the consumer is cancelled or the connection closes
				wg.Add(1)
				deliveriesReceived.Add(handlerCtx, 1, queueAttr)
				go func() {
					defer wg.Done()
					inflight.Add(handlerCtx, 1, queueAttr)
					started := time.Now()
					ctx, span := startConsumerSpan(handlerCtx, c.Queue, d)
					defer func() {
						span.End()
						inflight.Add(handlerCtx, -1, queueAttr)
						processingDuration.Record(handlerCtx, time.Since(started).Seconds(), queueAttr)
					}()
					c.Handler(ctx, d, pub)
				}()
			}
		}(c)
	}

	s.Log.Info("amqp session started", "prefetch", s.Prefetch)
	s.OnState(true)

	var result error
	select {
	case <-ctx.Done():
		// Stop receiving new deliveries; in-flight ones are finished below.
		for _, c := range s.Consumers {
			_ = consCh.Cancel(c.Tag, false)
		}
	case amqpErr := <-connClosed:
		result = fmt.Errorf("connection closed: %v", amqpErr)
	}

	// Let in-flight handlers finish (they ack/nack on the still-open channel), then stop.
	done := make(chan struct{})
	go func() { dispatchers.Wait(); wg.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(drainTimeout):
		s.Log.Warn("in-flight handlers did not finish before shutdown")
	}
	return result
}
