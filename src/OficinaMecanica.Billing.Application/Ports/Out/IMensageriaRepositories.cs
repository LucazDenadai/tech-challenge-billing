using OficinaMecanica.Billing.Application.Contratos.Saga;

namespace OficinaMecanica.Billing.Application.Ports.Out;

public interface IInboxRepository
{
    // Registra o messageId dentro da transação corrente. Retorna false quando já foi registrado (duplicata).
    Task<bool> RegistrarAsync(MensagemSaga mensagem, string canal, string payload, CancellationToken ct = default);
}

public interface IOutbox
{
    // Inclui o evento na transação corrente; o despachante publica depois do commit (ADR-016).
    void Adicionar(MensagemSaga mensagem);
}

public interface ITransacao
{
    // Inbox, efeito e outbox em uma transação: ou tudo é confirmado, ou nada.
    Task<T> ExecutarAsync<T>(Func<Task<T>> acao, CancellationToken ct = default);
}
