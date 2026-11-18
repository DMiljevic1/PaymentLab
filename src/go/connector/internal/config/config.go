package config

import (
	"os"
	"strconv"
	"time"
)

type Config struct {
	AmqpURL         string
	BankURL         string
	Prefetch        int           // unacked messages per consumer = max concurrent bank calls per queue
	BankTimeout     time.Duration // HTTP timeout for one bank call
	MaxAttempts     int           // attempts per transfer before it goes to the DLQ
	RetryDelay      time.Duration // TTL of the retry queue
	HealthAddr      string
	ShutdownTimeout time.Duration
}

func Load() Config {
	return Config{
		AmqpURL:         env("AMQP_URL", "amqp://lab:lab@localhost:5672/"),
		BankURL:         env("BANK_URL", "http://localhost:8090"),
		Prefetch:        envInt("PREFETCH", 20),
		BankTimeout:     time.Duration(envInt("BANK_TIMEOUT_MS", 5000)) * time.Millisecond,
		MaxAttempts:     envInt("MAX_ATTEMPTS", 5),
		RetryDelay:      time.Duration(envInt("RETRY_DELAY_MS", 5000)) * time.Millisecond,
		HealthAddr:      env("HEALTH_ADDR", ":8080"),
		ShutdownTimeout: 10 * time.Second,
	}
}

func env(k, def string) string {
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
