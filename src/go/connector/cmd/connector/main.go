// Connector: consumes bank commands from RabbitMQ, calls the bank, publishes results.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"sync/atomic"
	"syscall"
	"time"

	"paymentlab/connector/internal/bank"
	"paymentlab/connector/internal/broker"
	"paymentlab/connector/internal/config"
	"paymentlab/connector/internal/handler"
	"paymentlab/connector/internal/messages"
	"paymentlab/connector/internal/telemetry"
)

func main() {
	cfg := config.Load()

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	log, shutdownTelemetry, err := telemetry.Setup(ctx, "connector")
	if err != nil {
		slog.Error("telemetry setup failed", "error", err)
		os.Exit(1)
	}

	var connected atomic.Bool
	health := &http.Server{Addr: cfg.HealthAddr, ReadHeaderTimeout: 5 * time.Second, Handler: healthMux(&connected)}
	go func() {
		if err := health.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("health server failed", "error", err)
		}
	}()

	h := &handler.Handler{
		Bank:        bank.NewClient(cfg.BankURL, cfg.BankTimeout),
		MaxAttempts: cfg.MaxAttempts,
		Log:         log,
	}
	session := &broker.Session{
		URL:        cfg.AmqpURL,
		Prefetch:   cfg.Prefetch,
		RetryDelay: cfg.RetryDelay,
		Log:        log,
		OnState:    connected.Store,
		Consumers: []broker.Consumer{
			{Queue: messages.TransfersQueue, Tag: "connector-transfers", Handler: h.HandleTransfer},
			{Queue: messages.StatusQueue, Tag: "connector-status", Handler: h.HandleStatus},
		},
	}

	log.Info("connector starting", "bankUrl", cfg.BankURL, "prefetch", cfg.Prefetch, "bankTimeout", cfg.BankTimeout.String())
	session.Run(ctx, cfg.ShutdownTimeout)

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = health.Shutdown(shutdownCtx)
	log.Info("connector stopped")
	_ = shutdownTelemetry(shutdownCtx)
}

func healthMux(connected *atomic.Bool) *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) })
	mux.HandleFunc("GET /ready", func(w http.ResponseWriter, _ *http.Request) {
		if connected.Load() {
			w.WriteHeader(http.StatusOK)
			return
		}
		w.WriteHeader(http.StatusServiceUnavailable)
	})
	return mux
}
