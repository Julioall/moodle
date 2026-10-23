namespace Moodle.Domain;

public readonly record struct ConfirmationToken(string Value)
{
    public override string ToString() => Value;
}
