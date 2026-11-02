namespace PodereBot.Lib.Commands;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class CommandMetadataAttribute() : Attribute
{
    public required string Key { get; init; }
    public required string Description { get; init; }
    public bool Admin { get; init; } = false;
}
