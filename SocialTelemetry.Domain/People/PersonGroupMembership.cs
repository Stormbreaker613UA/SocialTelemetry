namespace SocialTelemetry.Domain.People;

public sealed class PersonGroupMembership
{
    public Guid GroupId { get; set; }
    public Guid PersonId { get; set; }
    public PersonGroup Group { get; set; } = null!;
    public Person Person { get; set; } = null!;
}
