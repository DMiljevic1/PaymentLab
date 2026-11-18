// Package handler turns bank commands from RabbitMQ into bank calls and publishes the results.
package handler

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"strings"
	"time"

	"github.com/google/uuid"
	amqp "github.com/rabbitmq/amqp091-go"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/trace"

	"paymentlab/connector/internal/bank"
	"paymentlab/connector/internal/broker"
	"paymentlab/connector/internal/messages"
)

type Handler struct {
	Bank        *bank.Client
	MaxAttempts int
	Log         *slog.Logger
}

const publishTimeout = 10 * time.Second

// HandleTransfer: call the bank; publish the result, then ack.
// Transient bank errors are retried through the delay queue; after MaxAttempts the command is dead-lettered
// and the Payments saga finds out through its own timeout + status inquiry.
func (h *Handler) HandleTransfer(ctx context.Context, d amqp.Delivery, pub *broker.Publisher) {
	var cmd messages.SubmitBankTransfer
	if err := json.Unmarshal(d.Body, &cmd); err != nil || cmd.PaymentID == "" {
		h.Log.ErrorContext(ctx, "malformed transfer command, dead-lettering", "messageId", d.MessageId, "error", err)
		_ = d.Nack(false, false)
		settled(ctx, messages.TransfersQueue, "malformed", false)
		return
	}
	attempt := attemptOf(d)
	log := h.Log.With("paymentId", cmd.PaymentID, "attempt", attempt)
	trace.SpanFromContext(ctx).SetAttributes(attribute.Int("connector.attempt", attempt))

	started := time.Now()
	res, err := h.Bank.Submit(ctx, cmd)
	if err != nil {
		bankCall(ctx, "submit", "error", started)
		if errors.Is(err, bank.ErrTransient) && attempt < h.MaxAttempts {
			log.WarnContext(ctx, "bank call failed, scheduling retry", "error", err)
			h.retry(ctx, d, attempt, pub, log)
			return
		}
		log.ErrorContext(ctx, "bank call failed, giving up (dead-letter)", "error", err)
		_ = d.Nack(false, false)
		settled(ctx, messages.TransfersQueue, "dead_lettered", false)
		return
	}

	outcome := messages.OutcomeRejected
	if res.Accepted {
		outcome = messages.OutcomeAccepted
	}
	bankCall(ctx, "submit", strings.ToLower(outcome), started)
	h.publishResult(ctx, messages.TransfersQueue, d, pub, log, messages.BankTransferResult{
		MessageID:     uuid.NewString(),
		PaymentID:     cmd.PaymentID,
		Outcome:       outcome,
		BankReference: res.BankReference,
		ReasonCode:    res.ReasonCode,
		CompletedAt:   time.Now().UTC(),
	})
}

// HandleStatus: ask the bank whether it knows the transfer and report what it says.
func (h *Handler) HandleStatus(ctx context.Context, d amqp.Delivery, pub *broker.Publisher) {
	var q messages.QueryBankTransferStatus
	if err := json.Unmarshal(d.Body, &q); err != nil || q.PaymentID == "" {
		h.Log.ErrorContext(ctx, "malformed status query, dead-lettering", "messageId", d.MessageId, "error", err)
		_ = d.Nack(false, false)
		settled(ctx, messages.StatusQueue, "malformed", false)
		return
	}
	log := h.Log.With("paymentId", q.PaymentID, "inquiry", q.Inquiry)

	result := messages.BankTransferResult{MessageID: uuid.NewString(), PaymentID: q.PaymentID, CompletedAt: time.Now().UTC()}
	started := time.Now()
	res, found, err := h.Bank.Status(ctx, q.PaymentID)
	switch {
	case err != nil:
		log.WarnContext(ctx, "status inquiry failed", "error", err)
		result.Outcome = messages.OutcomeUnknown
	case !found:
		result.Outcome = messages.OutcomeNotFound
	case res.Accepted:
		result.Outcome, result.BankReference = messages.OutcomeAccepted, res.BankReference
	default:
		result.Outcome, result.BankReference, result.ReasonCode = messages.OutcomeRejected, res.BankReference, res.ReasonCode
	}
	if err != nil {
		bankCall(ctx, "status", "error", started)
	} else {
		bankCall(ctx, "status", strings.ToLower(result.Outcome), started)
	}
	h.publishResult(ctx, messages.StatusQueue, d, pub, log, result)
}

func (h *Handler) publishResult(ctx context.Context, queue string, d amqp.Delivery, pub *broker.Publisher, log *slog.Logger, r messages.BankTransferResult) {
	body, _ := json.Marshal(r)
	pctx, cancel := context.WithTimeout(ctx, publishTimeout)
	defer cancel()

	err := pub.Publish(pctx, messages.EventsExchange, messages.TransferResultKey, amqp.Publishing{
		MessageId:     r.MessageID,
		CorrelationId: r.PaymentID,
		Type:          messages.TypeBankTransferResult,
		ContentType:   "application/json",
		DeliveryMode:  amqp.Persistent,
		Timestamp:     time.Now().UTC(),
		Body:          body,
	})
	if err != nil {
		// Not acked: the command is redelivered and the bank call repeated (idempotent at the bank).
		log.ErrorContext(ctx, "publishing result failed, requeueing command", "error", err)
		_ = d.Nack(false, true)
		settled(ctx, queue, "requeued", false)
		return
	}
	_ = d.Ack(false)
	settled(ctx, queue, "published", true)
	log.InfoContext(ctx, "bank result published", "outcome", r.Outcome, "bankReference", r.BankReference, "reason", r.ReasonCode)
}

func (h *Handler) retry(ctx context.Context, d amqp.Delivery, attempt int, pub *broker.Publisher, log *slog.Logger) {
	headers := amqp.Table{messages.HeaderAttempt: int32(attempt + 1)}
	pctx, cancel := context.WithTimeout(ctx, publishTimeout)
	defer cancel()

	err := pub.Publish(pctx, messages.RetryExchange, messages.TransfersQueue, amqp.Publishing{
		MessageId:     d.MessageId,
		CorrelationId: d.CorrelationId,
		Type:          d.Type,
		ContentType:   d.ContentType,
		DeliveryMode:  amqp.Persistent,
		Timestamp:     time.Now().UTC(),
		Headers:       headers,
		Body:          d.Body,
	})
	if err != nil {
		log.ErrorContext(ctx, "scheduling retry failed, requeueing", "error", err)
		_ = d.Nack(false, true)
		settled(ctx, messages.TransfersQueue, "requeued", false)
		return
	}
	_ = d.Ack(false)
	settled(ctx, messages.TransfersQueue, "retry_scheduled", false)
}

func attemptOf(d amqp.Delivery) int {
	switch v := d.Headers[messages.HeaderAttempt].(type) {
	case int32:
		return int(v)
	case int64:
		return int(v)
	case int16:
		return int(v)
	case int8:
		return int(v)
	case int:
		return v
	case uint8:
		return int(v)
	default:
		return 1
	}
}
