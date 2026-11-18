package handler

import (
	"context"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

var (
	meter = otel.Meter("paymentlab/connector/handler")

	handled, _ = meter.Int64Counter("connector.messages.handled",
		metric.WithUnit("{message}"), metric.WithDescription("Deliveries by how they were settled"))
	bankCalls, _ = meter.Int64Counter("bank.calls",
		metric.WithUnit("{call}"), metric.WithDescription("Calls to the bank by operation and outcome"))
	bankCallDuration, _ = meter.Float64Histogram("bank.call.duration",
		metric.WithUnit("s"), metric.WithDescription("Bank call latency as seen by the connector"),
		metric.WithExplicitBucketBoundaries(0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10))
)

// settled records how a delivery ended and marks the span as failed for anything but success.
func settled(ctx context.Context, queue, result string, ok bool) {
	handled.Add(ctx, 1, metric.WithAttributes(attribute.String("queue", queue), attribute.String("result", result)))
	span := trace.SpanFromContext(ctx)
	span.SetAttributes(attribute.String("connector.result", result))
	if !ok {
		span.SetStatus(codes.Error, result)
	}
}

func bankCall(ctx context.Context, operation, outcome string, started time.Time) {
	attrs := metric.WithAttributes(attribute.String("operation", operation), attribute.String("outcome", outcome))
	bankCalls.Add(ctx, 1, attrs)
	bankCallDuration.Record(ctx, time.Since(started).Seconds(), attrs)
	trace.SpanFromContext(ctx).SetAttributes(attribute.String("bank.outcome", outcome))
}
