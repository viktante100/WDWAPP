namespace WDWAPP.Services;

// Replace with a locally cached provider once Veizla supplies a supported feed and stable identity contract.
// Public rendering never depends on an external HTTP request or ambiguous name matching.
public interface IPlayerRanking
{
    string Display(string? veizlaIdentity);
}
public sealed class UnavailablePlayerRanking : IPlayerRanking
{
    public string Display(string? veizlaIdentity) => "N/A";
}
