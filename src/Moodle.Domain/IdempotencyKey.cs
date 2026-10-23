namespace Moodle.Domain;

public readonly record struct IdempotencyKey(string Value)
{
    public static IdempotencyKey New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}
