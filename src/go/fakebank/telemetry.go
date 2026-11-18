// setupTelemetry sets up OpenTelemetry (traces, metrics, logs over OTLP/gRPC) and a slog logger that
// writes JSON to stdout with trace ids and also forwards every record to the OTel log pipeline.
// The collector endpoint comes from OTEL_EXPORTER_OTLP_ENDPOINT (set in docker-compose).
package main

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"time"

	"go.opentelemetry.io/contrib/bridges/otelslog"
	"go.opentelemetry.io/contrib/instrumentation/runtime"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetricgrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/propagation"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.40.0"
	"go.opentelemetry.io/otel/trace"
)

// setupTelemetry installs global tracer/meter providers and the propagator, and returns a logger and a shutdown func.
func setupTelemetry(ctx context.Context, service string) (*slog.Logger, func(context.Context) error, error) {
	host, _ := os.Hostname()
	res, err := resource.Merge(resource.Default(), resource.NewSchemaless(
		semconv.ServiceName(service),
		semconv.ServiceNamespace("paymentlab"),
		semconv.ServiceInstanceID(host),
	))
	if err != nil {
		return nil, nil, err
	}

	traceExp, err := otlptracegrpc.New(ctx)
	if err != nil {
		return nil, nil, err
	}
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithResource(res),
		sdktrace.WithBatcher(traceExp),
		sdktrace.WithSampler(sdktrace.ParentBased(dropOrphanClientSpans{})),
	)

	metricExp, err := otlpmetricgrpc.New(ctx)
	if err != nil {
		return nil, nil, err
	}
	mp := sdkmetric.NewMeterProvider(
		sdkmetric.WithResource(res),
		sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metricExp, sdkmetric.WithInterval(10*time.Second))),
	)

	logExp, err := otlploggrpc.New(ctx)
	if err != nil {
		return nil, nil, err
	}
	lp := sdklog.NewLoggerProvider(sdklog.WithResource(res), sdklog.WithProcessor(sdklog.NewBatchProcessor(logExp)))

	otel.SetTracerProvider(tp)
	otel.SetMeterProvider(mp)
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(propagation.TraceContext{}, propagation.Baggage{}))
	if err := runtime.Start(); err != nil {
		return nil, nil, err
	}

	logger := slog.New(fanout{
		traceIDs{slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})},
		otelslog.NewHandler(service, otelslog.WithLoggerProvider(lp)),
	}).With("service", service)

	shutdown := func(ctx context.Context) error {
		return errors.Join(tp.Shutdown(ctx), mp.Shutdown(ctx), lp.Shutdown(ctx))
	}
	return logger, shutdown, nil
}

// dropOrphanClientSpans drops root spans of kind Client (calls made outside any request/message);
// everything else is sampled. Used as the root sampler inside ParentBased.
type dropOrphanClientSpans struct{}

func (dropOrphanClientSpans) ShouldSample(p sdktrace.SamplingParameters) sdktrace.SamplingResult {
	if p.Kind == trace.SpanKindClient {
		return sdktrace.SamplingResult{Decision: sdktrace.Drop}
	}
	return sdktrace.SamplingResult{Decision: sdktrace.RecordAndSample, Tracestate: trace.SpanContextFromContext(p.ParentContext).TraceState()}
}

func (dropOrphanClientSpans) Description() string { return "DropOrphanClientSpans" }

// traceIDs adds trace_id / span_id to stdout JSON logs when the record's context carries a span.
type traceIDs struct{ slog.Handler }

func (h traceIDs) Handle(ctx context.Context, r slog.Record) error {
	if sc := trace.SpanContextFromContext(ctx); sc.IsValid() {
		r.AddAttrs(slog.String("trace_id", sc.TraceID().String()), slog.String("span_id", sc.SpanID().String()))
	}
	return h.Handler.Handle(ctx, r)
}
func (h traceIDs) WithAttrs(a []slog.Attr) slog.Handler { return traceIDs{h.Handler.WithAttrs(a)} }
func (h traceIDs) WithGroup(n string) slog.Handler      { return traceIDs{h.Handler.WithGroup(n)} }

// fanout sends each record to several handlers.
type fanout []slog.Handler

func (f fanout) Enabled(ctx context.Context, l slog.Level) bool {
	for _, h := range f {
		if h.Enabled(ctx, l) {
			return true
		}
	}
	return false
}

func (f fanout) Handle(ctx context.Context, r slog.Record) error {
	var errs []error
	for _, h := range f {
		if h.Enabled(ctx, r.Level) {
			errs = append(errs, h.Handle(ctx, r.Clone()))
		}
	}
	return errors.Join(errs...)
}

func (f fanout) WithAttrs(a []slog.Attr) slog.Handler {
	out := make(fanout, len(f))
	for i, h := range f {
		out[i] = h.WithAttrs(a)
	}
	return out
}

func (f fanout) WithGroup(n string) slog.Handler {
	out := make(fanout, len(f))
	for i, h := range f {
		out[i] = h.WithGroup(n)
	}
	return out
}
