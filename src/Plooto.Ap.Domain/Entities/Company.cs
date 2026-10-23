namespace Plooto.Ap.Domain.Entities;

public class Company
{
    private Company() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;           // tenant key
    public string ApiKeyHash { get; private set; } = default!;     // SHA-256 hex of the bearer key; plaintext is never stored
    public DateTimeOffset CreatedAt { get; private set; }

    public static Company Create(string name, string slug, string apiKeyHash)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(slug)) throw new ArgumentException("Slug is required.", nameof(slug));
        if (string.IsNullOrWhiteSpace(apiKeyHash)) throw new ArgumentException("ApiKeyHash is required.", nameof(apiKeyHash));
        return new Company
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Slug = slug.Trim().ToLowerInvariant(),
            ApiKeyHash = apiKeyHash,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
