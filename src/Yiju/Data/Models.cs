namespace Yiju.Data;

public sealed class Sentence
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class SentenceList
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<Sentence> Items { get; set; } = new();

    public override string ToString() => Name;
}
