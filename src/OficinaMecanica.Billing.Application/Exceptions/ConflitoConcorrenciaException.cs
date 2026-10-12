namespace OficinaMecanica.Billing.Application.Exceptions;

// Outra operação alterou o mesmo registro (por exemplo, o saldo da mesma peça) entre a leitura
// e a gravação. Na API vira 409; no consumidor da Saga é falha transitória e a mensagem é refeita.
public class ConflitoConcorrenciaException : Exception
{
    public ConflitoConcorrenciaException(Exception inner)
        : base("O recurso foi alterado por outra operação simultânea. Tente novamente.", inner) { }
}
