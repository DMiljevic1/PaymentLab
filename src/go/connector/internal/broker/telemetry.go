package broker

import (
	"context"

	amqp "github.com/rabbitmq/amqp091-go"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

const instrumentationName = "paymentlab/connector/broker"

var (
	tracer = otel.Tracer(instrumentationName)
	meter  = otel.Meter(instrumentationName)

	inflight, _ = meter.Int64UpDownCounter("connector.messages.inflight",
		metric.WithUnit("{message}"), metric.WithDescription("Deliveries currently being handled"))
	processingDuration, _ = meter.Float64Histogram("connector.message.processing.duration",
		metric.WithUnit("s"), metric.WithDescription("Time to handle one delivery (bank call + publish + ack)"),
		metric.WithExplicitBucketBoundaries(0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30))
	deliveriesReceived, _ = meter.Int64Counter("connector.deliveries",
		metric.WithUnit("{message}"), metric.WithDescription("Deliveries received from RabbitMQ"))
	reconnects, _ = meter.Int64Counter("connector.amqp.reconnects",
		metric.WithUnit("{reconnect}"), metric.WithDescription("AMQP session restarts"))
	publishes, _ = meter.Int64Counter("connector.publishes",
		metric.WithUnit("{message}"), metric.WithDescription("Messages published (confirmed or failed)"))
	publishDuration, _ = meter.Float64Histogram("connector.publish.duration",
		metric.WithUnit("s"), metric.WithDescription("Publish + broker confirm latency"),
		metric.WithExplicitBucketBoundaries(0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10))
)

// HeaderCarrier adapts AMQP headers for W3C trace-context propagation.
type HeaderCarrier amqp.Table

func (c HeaderCarrier) Get(key string) string {
	switch v := c[key].(type) {
	case string:
		return v
	case []byte:
		return string(v)
	default:
		return ""
	}
}

func (c HeaderCarrier) Set(key, value string) { c[key] = value }

func (c HeaderCarrier) Keys() []string {
	keys := make([]string, 0, len(c))
	for k := range c {
		keys = append(keys, k)
	}
	return keys
}

// startConsumerSpan continues the trace carried in the delivery headers.
func startConsumerSpan(ctx context.Context, queue string, d amqp.Delivery) (context.Context, trace.Span) {
	ctx = otel.GetTextMapPropagator().Extract(ctx, HeaderCarrier(d.Headers))
	return tracer.Start(ctx, queue+" process",
		trace.WithSpanKind(trace.SpanKindConsumer),
		trace.WithAttributes(
			attribute.String("messaging.system", "rabbitmq"),
			attribute.String("messaging.operation.type", "process"),
			attribute.String("messaging.destination.name", queue),
			attribute.String("messaging.message.id", d.MessageId),
			attribute.String("payment.id", d.CorrelationId),
			attribute.Bool("messaging.rabbitmq.redelivered", d.Redelivered),
		))
}
