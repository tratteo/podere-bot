namespace PodereBot.Lib.Commands;

internal class CommandRegistryKey(CommandMetadataAttribute metadata, Type commandType)
{
    public readonly CommandMetadataAttribute metadata = metadata;
    public readonly Type commandType = commandType;
}

internal class CommandRegistryKey<T>(CommandMetadataAttribute metadata)
    : CommandRegistryKey(metadata, typeof(T))
    where T : Command { }
