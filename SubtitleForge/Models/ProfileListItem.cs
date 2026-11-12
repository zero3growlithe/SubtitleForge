namespace SubtitleForge.Models
{
    /// <summary>Combo box entry for a profile: the id stays hidden, the name is what you see.</summary>
    public sealed class ProfileListItem
    {
        public ProfileListItem(string? id, string name)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
        }

        public string? Id { get; private set; }

        public string Name { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
