// Fake Bank: a simulated external bank API with configurable latency and failure behaviour.
//
//	POST /v1/transfers        (header Idempotency-Key)  -> 201 new / 200 replay
//	GET  /v1/transfers/{key}                            -> 200 / 404
//	GET  /admin/config, POST /admin/config (partial JSON update)
//	GET  /healthz
//
// Deterministic test hook: a creditor IBAN ending in "9999" is always rejected (AC04, closed account).
package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

type Config struct {
	LatencyMs        int     `json:"latencyMs"`        // base processing time
	JitterMs         int     `json:"jitterMs"`         // + uniform random 0..jitter
	RejectRate       float64 `json:"rejectRate"`       // probability of a business rejection (AG01)
	ErrorRate        float64 `json:"errorRate"`        // probability of HTTP 503 (nothing stored)
	SlowRate         float64 `json:"slowRate"`         // probability of a very slow response (transfer IS executed)
	SlowMs           int     `json:"slowMs"`           // duration of a slow response
	RetentionMinutes int     `json:"retentionMinutes"` // how long transfers are kept for status queries
}

type Transfer struct {
	Key             string    `json:"idempotencyKey"`
	BankReference   string    `json:"bankReference"`
	Status          string    `json:"status"` // ACCEPTED | REJECTED
	ReasonCode      string    `json:"reasonCode,omitempty"`
	AmountMinor     int64     `json:"amountMinor"`
	Currency        string    `json:"currency"`
	CreditorIban    string    `json:"creditorIban"`
	DebtorAccountID string    `json:"debtorAccountId"`
	Reference       string    `json:"reference,omitempty"`
	CreatedAt       time.Time `json:"createdAt"`
}

type transferRequest struct {
	AmountMinor     int64  `json:"amountMinor"`
	Currency        string `json:"currency"`
	CreditorIban    string `json:"creditorIban"`
	DebtorAccountID string `json:"debtorAccountId"`
	Reference       string `json:"reference"`
}

type bank struct {
	cfg       atomic.Pointer[Config]
	mu        sync.RWMutex
	transfers map[string]*Transfer
	seq       atomic.Int64
	log       *slog.Logger
}

func main() {
	log := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})).With("service", "fakebank")
	b := &bank{transfers: make(map[string]*Transfer), log: log}
	cfg := Config{
		LatencyMs:        envInt("FAKEBANK_LATENCY_MS", 50),
		JitterMs:         envInt("FAKEBANK_JITTER_MS", 50),
		RejectRate:       envFloat("FAKEBANK_REJECT_RATE", 0.02),
		ErrorRate:        envFloat("FAKEBANK_ERROR_RATE", 0),
		SlowRate:         envFloat("FAKEBANK_SLOW_RATE", 0),
		SlowMs:           envInt("FAKEBANK_SLOW_MS", 10000),
		RetentionMinutes: envInt("FAKEBANK_RETENTION_MINUTES", 60),
	}
	b.cfg.Store(&cfg)

	mux := http.NewServeMux()
	mux.HandleFunc("POST /v1/transfers", b.createTransfer)
	mux.HandleFunc("GET /v1/transfers/{key}", b.getTransfer)
	mux.HandleFunc("GET /admin/config", b.getConfig)
	mux.HandleFunc("POST /admin/config", b.setConfig)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) })

	addr := ":" + envStr("PORT", "8080")
	srv := &http.Server{Addr: addr, Handler: mux, ReadHeaderTimeout: 5 * time.Second}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go b.sweep(ctx)

	go func() {
		log.Info("fake bank listening", "addr", addr, "config", cfg)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("server failed", "error", err)
			os.Exit(1)
		}
	}()
	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = srv.Shutdown(shutdownCtx)
}

func (b *bank) createTransfer(w http.ResponseWriter, r *http.Request) {
	key := r.Header.Get("Idempotency-Key")
	if key == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "Idempotency-Key header is required"})
		return
	}
	var req transferRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.AmountMinor <= 0 || req.CreditorIban == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid transfer request"})
		return
	}

	b.mu.RLock()
	existing, ok := b.transfers[key]
	b.mu.RUnlock()
	if ok {
		writeJSON(w, http.StatusOK, existing)
		return
	}

	cfg := b.cfg.Load()
	delay := time.Duration(cfg.LatencyMs) * time.Millisecond
	if cfg.JitterMs > 0 {
		delay += time.Duration(rand.IntN(cfg.JitterMs)) * time.Millisecond
	}
	slow := rand.Float64() < cfg.SlowRate
	if slow {
		delay = time.Duration(cfg.SlowMs) * time.Millisecond
	}

	if rand.Float64() < cfg.ErrorRate {
		sleep(r.Context(), delay)
		b.log.Warn("simulated bank error", "key", key)
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"error": "service temporarily unavailable"})
		return
	}

	t := &Transfer{
		Key:             key,
		BankReference:   fmt.Sprintf("FB-%s-%08d", time.Now().UTC().Format("20060102"), b.seq.Add(1)),
		Status:          "ACCEPTED",
		AmountMinor:     req.AmountMinor,
		Currency:        req.Currency,
		CreditorIban:    req.CreditorIban,
		DebtorAccountID: req.DebtorAccountID,
		Reference:       req.Reference,
		CreatedAt:       time.Now().UTC(),
	}
	switch {
	case strings.HasSuffix(req.CreditorIban, "9999"):
		t.Status, t.ReasonCode = "REJECTED", "AC04"
	case rand.Float64() < cfg.RejectRate:
		t.Status, t.ReasonCode = "REJECTED", "AG01"
	}

	// The bank books the transfer before it answers: a slow answer that the client gives up on
	// still leaves an executed transfer behind (visible via GET /v1/transfers/{key}).
	b.mu.Lock()
	if prev, dup := b.transfers[key]; dup {
		t = prev
	} else {
		b.transfers[key] = t
	}
	b.mu.Unlock()

	sleep(r.Context(), delay)
	b.log.Info("transfer processed", "key", key, "status", t.Status, "reason", t.ReasonCode, "slow", slow,
		"amountMinor", t.AmountMinor, "latencyMs", delay.Milliseconds())
	writeJSON(w, http.StatusCreated, t)
}

func (b *bank) getTransfer(w http.ResponseWriter, r *http.Request) {
	key := r.PathValue("key")
	b.mu.RLock()
	t, ok := b.transfers[key]
	b.mu.RUnlock()
	if !ok {
		writeJSON(w, http.StatusNotFound, map[string]string{"error": "transfer not found"})
		return
	}
	writeJSON(w, http.StatusOK, t)
}

func (b *bank) getConfig(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, b.cfg.Load())
}

func (b *bank) setConfig(w http.ResponseWriter, r *http.Request) {
	next := *b.cfg.Load()
	if err := json.NewDecoder(r.Body).Decode(&next); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": err.Error()})
		return
	}
	b.cfg.Store(&next)
	b.log.Info("config updated", "config", next)
	writeJSON(w, http.StatusOK, next)
}

// sweep removes transfers older than the retention period so memory stays bounded.
func (b *bank) sweep(ctx context.Context) {
	ticker := time.NewTicker(time.Minute)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			cutoff := time.Now().Add(-time.Duration(b.cfg.Load().RetentionMinutes) * time.Minute)
			removed := 0
			b.mu.Lock()
			for k, t := range b.transfers {
				if t.CreatedAt.Before(cutoff) {
					delete(b.transfers, k)
					removed++
				}
			}
			size := len(b.transfers)
			b.mu.Unlock()
			if removed > 0 {
				b.log.Info("swept old transfers", "removed", removed, "remaining", size)
			}
		}
	}
}

func sleep(ctx context.Context, d time.Duration) {
	if d <= 0 {
		return
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
	case <-t.C:
	}
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func envStr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func envInt(k string, def int) int {
	if v, err := strconv.Atoi(os.Getenv(k)); err == nil {
		return v
	}
	return def
}

func envFloat(k string, def float64) float64 {
	if v, err := strconv.ParseFloat(os.Getenv(k), 64); err == nil {
		return v
	}
	return def
}
