namespace TinkerCAN.Core;

public record PortInfo(string Name, string Description)
{
    public string DisplayName => string.IsNullOrEmpty(Description) ? Name : $"{Name} — {Description}";
    public override string ToString() => DisplayName;
}
