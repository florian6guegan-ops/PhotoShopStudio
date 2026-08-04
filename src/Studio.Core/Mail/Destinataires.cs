namespace Studio.Core.Mail;

/// <summary>
/// Lecture d'une saisie d'adresses au comptoir.
///
/// L'opérateur tape ce qu'on lui dicte, et la façon de séparer deux adresses n'est jamais
/// la même : un point-virgule, une virgule, un espace, ou une adresse par ligne quand il
/// colle ce qu'un client lui a envoyé. Les quatre sont admises — refuser la saisie parce
/// que le séparateur n'est pas le bon ferait perdre du temps devant le client.
/// </summary>
public static class Destinataires
{
    private static readonly char[] Separateurs = [';', ',', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// Les adresses d'une saisie, dans l'ordre, sans doublon.
    ///
    /// Le doublon est écarté sans le dire : la même adresse tapée deux fois est une faute
    /// de frappe, pas une demande d'envoyer deux fois.
    /// </summary>
    public static IReadOnlyList<string> Analyser(string? saisie)
    {
        if (string.IsNullOrWhiteSpace(saisie)) return [];

        var vues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adresses = new List<string>();

        foreach (var morceau in saisie.Split(Separateurs, StringSplitOptions.RemoveEmptyEntries))
        {
            var adresse = morceau.Trim();
            if (adresse.Length > 0 && vues.Add(adresse)) adresses.Add(adresse);
        }

        return adresses;
    }

    /// <summary>
    /// Contrôle volontairement grossier : une arobase entourée de quelque chose, et un
    /// point après. On ne cherche pas à valider une adresse — seul le serveur sait — mais
    /// à rattraper la faute de frappe évidente avant de facturer.
    /// </summary>
    public static bool Recevable(string? adresse)
    {
        if (string.IsNullOrWhiteSpace(adresse)) return false;

        var propre = adresse.Trim();
        var arobase = propre.IndexOf('@');
        if (arobase <= 0 || arobase != propre.LastIndexOf('@')) return false;

        var domaine = propre[(arobase + 1)..];
        return domaine.Contains('.') && !domaine.StartsWith('.') && !domaine.EndsWith('.');
    }

    /// <summary>
    /// Les adresses de la saisie qui ne sont manifestement pas des adresses. Vide = tout
    /// va bien. Sert à nommer la faute plutôt qu'à griser un bouton sans rien expliquer.
    /// </summary>
    public static IReadOnlyList<string> Douteuses(string? saisie) =>
        Analyser(saisie).Where(a => !Recevable(a)).ToList();
}
