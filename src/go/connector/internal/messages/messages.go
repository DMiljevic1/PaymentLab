// Package messages holds the message contracts shared with the Payments service
// (see src/dotnet/Shared/PaymentLab.Contracts/Messaging.cs).
package messages

import "time"

const (
	CommandsExchange   = "payments.commands"
	EventsExchange     = "payments.events"
	RetryExchange      = "payments.retry"
	DeadLetterExchange = "payments.dlx"

	SubmitTransferKey = "bank.transfer.submit"
	QueryStatusKey    = "bank.transfer.status"
	TransferResultKey = "bank.transfer.result"

	TransfersQueue = "connector.bank-transfers"
	StatusQueue    = "connector.bank-status"

	TypeSubmitBankTransfer      = "SubmitBankTransfer"
	TypeQueryBankTransferStatus = "QueryBankTransferStatus"
	TypeBankTransferResult      = "BankTransferResult"

	HeaderTraceParent = "traceparent"
	HeaderTraceState  = "tracestate"
	HeaderAttempt     = "x-attempt"

	OutcomeAccepted = "Accepted"
	OutcomeRejected = "Rejected"
	OutcomeNotFound = "NotFound"
	OutcomeUnknown  = "Unknown"
)

type SubmitBankTransfer struct {
	MessageID       string    `json:"messageId"`
	PaymentID       string    `json:"paymentId"`
	Attempt         int       `json:"attempt"`
	AmountMinor     int64     `json:"amountMinor"`
	Currency        string    `json:"currency"`
	DebtorAccountID string    `json:"debtorAccountId"`
	CreditorIban    string    `json:"creditorIban"`
	Reference       string    `json:"reference,omitempty"`
	RequestedAt     time.Time `json:"requestedAt"`
}

type QueryBankTransferStatus struct {
	MessageID   string    `json:"messageId"`
	PaymentID   string    `json:"paymentId"`
	Inquiry     int       `json:"inquiry"`
	RequestedAt time.Time `json:"requestedAt"`
}

type BankTransferResult struct {
	MessageID     string    `json:"messageId"`
	PaymentID     string    `json:"paymentId"`
	Outcome       string    `json:"outcome"`
	BankReference string    `json:"bankReference,omitempty"`
	ReasonCode    string    `json:"reasonCode,omitempty"`
	CompletedAt   time.Time `json:"completedAt"`
}
