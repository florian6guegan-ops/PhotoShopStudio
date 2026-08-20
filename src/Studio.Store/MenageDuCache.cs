namespace Studio.Store;

/// <summary>
/// Le ménage du cache du poste : les copies de travail et les masques de détourage
/// s'effacent au bout de trente jours.
///
/// <b>Ce que ça règle.</b> Rien ne les purgeait. <c>cache\travail</c> reçoit une copie de
/// chaque photo ouverte au comptoir — c'est ce qui la sauve du retrait de la carte du
/// client — et il grossissait indéfiniment sur les quatre postes. <c>cache\masques</c>, qui
/// garde les détourages d'un lancement à l'autre, ferait pareil.
///
/// <b>Trente jours, comme l'historique</b> (voir <see cref="HistoriqueIdentite.Retention"/>),
/// et ce n'est pas une coïncidence : la fiche et les pixels qu'elle désigne doivent
/// disparaître ENSEMBLE. C'est déjà la règle du journal des bornes — « ce sont des photos de
/// clients, et une copie qu'on ne sait plus rattacher à personne n'a aucune raison de
/// rester ».
///
/// Les copies se datent par le NOM de leur dossier (<c>cache\travail\20260819\</c>) et non
/// par la date du fichier : c'est la même règle que l'archivage des commandes, et elle ne
/// dépend pas d'une date d'écriture qu'une sauvegarde ou une copie peuvent rafraîchir.
/// </summary>
public static class MenageDuCache
{
    /// <summary>Trente jours, comme l'historique des photos d'identité.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>
    /// Efface les dossiers de copies de travail plus vieux que la rétention.
    /// </summary>
    /// <param name="cacheDir">Le dossier <c>cache</c> du poste.</param>
    /// <returns>Nombre de dossiers effacés.</returns>
    public static int PurgerLesCopiesDeTravail(string cacheDir, DateTime? aujourdhui = null)
    {
        var racine = Path.Combine(cacheDir, "travail");
        if (!Directory.Exists(racine)) return 0;

        var plancher = (aujourdhui ?? DateTime.Today).Date - Retention;
        var efface = 0;

        foreach (var dossier in Directory.EnumerateDirectories(racine))
        {
            // nom : yyyyMMdd — la date du jour où les photos ont été ouvertes
            if (!DateTime.TryParseExact(Path.GetFileName(dossier), "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var jour))
                continue;

            if (jour >= plancher) continue;

            try
            {
                Directory.Delete(dossier, recursive: true);
                efface++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // un fichier ouvert dans l'explorateur : on repassera demain
            }
        }

        return efface;
    }

    /// <summary>
    /// Efface les masques de détourage plus vieux que la rétention.
    ///
    /// Sur la date d'écriture, faute de mieux : un masque n'a pas de journée à lui, il suit
    /// la photo. Un masque effacé alors que sa photo vit encore ne coûte qu'un détourage
    /// refait — jamais un réglage perdu.
    /// </summary>
    /// <param name="cacheDir">Le dossier <c>cache</c> du poste.</param>
    /// <returns>Nombre de masques effacés.</returns>
    public static int PurgerLesMasques(string cacheDir, DateTime? maintenant = null)
    {
        var racine = Path.Combine(cacheDir, "masques");
        if (!Directory.Exists(racine)) return 0;

        var plancher = (maintenant ?? DateTime.Now) - Retention;
        var efface = 0;

        foreach (var fichier in Directory.EnumerateFiles(racine, "*.png", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(fichier) >= plancher) continue;

                File.Delete(fichier);
                efface++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // fichier tenu : au prochain démarrage
            }
        }

        return efface;
    }
}
