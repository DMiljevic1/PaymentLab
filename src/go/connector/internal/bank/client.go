// Package bank is the HTTP client for the (fake) external bank.
package bank

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"

	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"

	"paymentlab/connector/internal/messages"
)

// ErrTransient means the outcome is unknown (timeout, connection error, 5xx): retry later.
var ErrTransient = errors.New("transient bank error")

type Result struct {
	Accepted      bool
	BankReference string
	ReasonCode    string
}

type Client struct {
	baseURL string
	http    *http.Client
}

func NewClient(baseURL string, timeout time.Duration) *Client {
	return &Client{
		baseURL: baseURL,
		http: &http.Client{
			Timeout: timeout,
			Transport: otelhttp.NewTransport(&http.Transport{
				MaxIdleConns:        100,
				MaxIdleConnsPerHost: 100,
				IdleConnTimeout:     90 * time.Second,
			}),
		},
	}
}

type transferResponse struct {
	BankReference string `json:"bankReference"`
	Status        string `json:"status"`
	ReasonCode    string `json:"reasonCode"`
}

// Submit sends a transfer. The payment id is the idempotency key, so a retry never pays twice.
func (c *Client) Submit(ctx context.Context, cmd messages.SubmitBankTransfer) (Result, error) {
	body, _ := json.Marshal(map[string]any{
		"amountMinor":     cmd.AmountMinor,
		"currency":        cmd.Currency,
		"creditorIban":    cmd.CreditorIban,
		"debtorAccountId": cmd.DebtorAccountID,
		"reference":       cmd.Reference,
	})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+"/v1/transfers", bytes.NewReader(body))
	if err != nil {
		return Result{}, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Idempotency-Key", cmd.PaymentID)

	resp, err := c.http.Do(req)
	if err != nil {
		return Result{}, fmt.Errorf("%w: %v", ErrTransient, err)
	}
	defer resp.Body.Close()

	switch {
	case resp.StatusCode == http.StatusOK || resp.StatusCode == http.StatusCreated:
		return decode(resp)
	case resp.StatusCode >= 400 && resp.StatusCode < 500:
		// The bank refused the request itself; retrying will not help.
		return Result{Accepted: false, ReasonCode: fmt.Sprintf("HTTP%d", resp.StatusCode)}, nil
	default:
		return Result{}, fmt.Errorf("%w: HTTP %d", ErrTransient, resp.StatusCode)
	}
}

// Status asks the bank whether it knows the transfer. found=false means the bank never received it.
func (c *Client) Status(ctx context.Context, paymentID string) (res Result, found bool, err error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.baseURL+"/v1/transfers/"+paymentID, nil)
	if err != nil {
		return Result{}, false, err
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return Result{}, false, fmt.Errorf("%w: %v", ErrTransient, err)
	}
	defer resp.Body.Close()

	switch resp.StatusCode {
	case http.StatusOK:
		r, err := decode(resp)
		return r, true, err
	case http.StatusNotFound:
		return Result{}, false, nil
	default:
		return Result{}, false, fmt.Errorf("%w: HTTP %d", ErrTransient, resp.StatusCode)
	}
}

func decode(resp *http.Response) (Result, error) {
	var tr transferResponse
	if err := json.NewDecoder(resp.Body).Decode(&tr); err != nil {
		return Result{}, fmt.Errorf("%w: bad response body: %v", ErrTransient, err)
	}
	return Result{Accepted: tr.Status == "ACCEPTED", BankReference: tr.BankReference, ReasonCode: tr.ReasonCode}, nil
}
