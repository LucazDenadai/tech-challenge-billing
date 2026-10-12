namespace OficinaMecanica.Billing.Application.Exceptions;

public class NotFoundException : Exception
{
    public NotFoundException(string entidade, object id)
        : base($"{entidade} '{id}' não encontrado(a).") { }
}
