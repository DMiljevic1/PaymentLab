package broker

import (
	"time"

	amqp "github.com/rabbitmq/amqp091-go"

	"paymentlab/connector/internal/messages"
)

// DeclareTopology declares the exchanges and the queues owned by the Connector. Declarations are idempotent.
//
//	payments.commands --bank.transfer.submit--> connector.bank-transfers --(nack)--> payments.dlx --> connector.bank-transfers.dlq
//	payments.retry    --connector.bank-transfers--> connector.bank-transfers.retry --(TTL)--> connector.bank-transfers
func DeclareTopology(ch *amqp.Channel, retryDelay time.Duration) error {
	for _, ex := range []struct{ name, kind string }{
		{messages.CommandsExchange, amqp.ExchangeDirect},
		{messages.EventsExchange, amqp.ExchangeTopic},
		{messages.RetryExchange, amqp.ExchangeDirect},
		{messages.DeadLetterExchange, amqp.ExchangeDirect},
	} {
		if err := ch.ExchangeDeclare(ex.name, ex.kind, true, false, false, false, nil); err != nil {
			return err
		}
	}

	if err := declareWorkQueue(ch, messages.TransfersQueue, messages.SubmitTransferKey, retryDelay); err != nil {
		return err
	}
	return declareWorkQueue(ch, messages.StatusQueue, messages.QueryStatusKey, retryDelay)
}

func declareWorkQueue(ch *amqp.Channel, queue, routingKey string, retryDelay time.Duration) error {
	dlq := queue + ".dlq"
	if _, err := ch.QueueDeclare(dlq, true, false, false, false, amqp.Table{"x-queue-type": "quorum"}); err != nil {
		return err
	}
	if err := ch.QueueBind(dlq, queue, messages.DeadLetterExchange, false, nil); err != nil {
		return err
	}

	if _, err := ch.QueueDeclare(queue, true, false, false, false, amqp.Table{
		"x-queue-type":              "quorum",
		"x-dead-letter-exchange":    messages.DeadLetterExchange,
		"x-dead-letter-routing-key": queue,
	}); err != nil {
		return err
	}
	if err := ch.QueueBind(queue, routingKey, messages.CommandsExchange, false, nil); err != nil {
		return err
	}

	// Delay queue: messages sit here for retryDelay, then expire back into the work queue (via the default exchange).
	retry := queue + ".retry"
	if _, err := ch.QueueDeclare(retry, true, false, false, false, amqp.Table{
		"x-queue-type":              "quorum",
		"x-message-ttl":             int32(retryDelay.Milliseconds()),
		"x-dead-letter-exchange":    "",
		"x-dead-letter-routing-key": queue,
	}); err != nil {
		return err
	}
	return ch.QueueBind(retry, queue, messages.RetryExchange, false, nil)
}
