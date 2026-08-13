using System.Text;

namespace Studio.Store;

/// <summary>
/// Écritures de fichiers jamais partielles : on écrit un .tmp complet puis on
/// l'échange atomiquement (File.Replace). En cas de coupure de courant, on
/// retrouve soit l'ancienne version intacte, soit la nouvelle — jamais un fichier tronqué.
///
/// <b>Lecture et écriture se croisent, et c'est la règle plutôt que l'exception.</b>
/// <see cref="OrderFolderStore.ScanRecent"/> relit TOUTES les commandes récentes à chaque
/// rafraîchissement d'écran ; l'impression, elle, réécrit <c>order.json</c> à chaque étape.
/// Il suffit donc d'imprimer pour que les deux se rencontrent. Les deux méthodes de cette
/// classe sont écrites pour ça — voir le détail sur chacune.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Nombre d'essais avant d'abandonner, et l'attente entre deux.</summary>
    /// <remarks>
    /// Volontairement court : l'échange se compte en microsecondes. Un fichier retenu plus
    /// de 80 ms l'est par autre chose — un antivirus, une sauvegarde, un éditeur resté
    /// ouvert — et ce cas-là doit remonter à l'appelant plutôt que de faire patienter.
    /// </remarks>
    private const int Essais = 5;
    private const int PauseMs = 20;

    /// <summary>
    /// Combien de fois céder la main devant une absence avant de la croire — voir
    /// <see cref="ReadAllTextOrNull"/>. Sans pause : ce qu'on attend dure quelques
    /// microsecondes.
    ///
    /// <b>Trois, et pas davantage, parce que ce sont des exceptions qu'on paie.</b> Mesuré
    /// sur le poste de Maisons-Alfort : 125 µs par appel sur un fichier absent contre 46 µs
    /// sur un fichier présent — l'écart, ce sont les quatre <c>FileNotFoundException</c>
    /// levées puis rattrapées. Une impression n'en fait qu'une poignée par enveloppe, donc
    /// c'est payable ; monter la constante ne le serait plus.
    ///
    /// <b>Et l'on ne peut pas éviter ces exceptions par un <c>File.Exists</c> préalable</b> :
    /// c'est précisément ce qui produisait le mirage, puisque pendant l'échange il rend faux
    /// lui aussi. Le seul test fiable est d'essayer d'ouvrir.
    /// </summary>
    private const int EssaisAbsence = 6;

    /// <summary>
    /// Combien des <see cref="EssaisAbsence"/> se contentent de céder la main, avant que les
    /// suivants ne dorment cinq millisecondes. Voir <see cref="ReadAllTextOrNull"/>.
    /// </summary>
    private const int EssaisSansAttendre = 3;

    /// <summary>
    /// Écrit le fichier d'un bloc.
    ///
    /// <b>L'échange final peut échouer à cause d'un simple LECTEUR.</b> C'est le piège de
    /// cette classe, et il est contre-intuitif : <c>File.Replace</c> a besoin de supprimer
    /// la cible, or un lecteur ordinaire (<c>File.ReadAllText</c>) l'ouvre en
    /// <c>FileShare.Read</c> — qui autorise d'autres lectures mais REFUSE la suppression.
    /// Une lecture en cours fait donc échouer l'écriture, et pas seulement l'inverse.
    ///
    /// C'est le plus grave des deux sens : une lecture ratée ne coûte qu'un compteur faux à
    /// l'écran, une écriture ratée perd l'état d'une commande en cours d'impression.
    /// <see cref="ReadAllTextOrNull"/> ne bloque plus l'échange, mais un lecteur ÉTRANGER —
    /// l'indexeur de Windows, un antivirus, un éditeur — le peut encore : d'où la reprise
    /// ici aussi.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        for (var essai = 1; ; essai++)
        {
            try
            {
                // ⚠ NE PAS remplacer par File.Move(tmp, path, overwrite: true). C'est
                // tentant — MoveFileEx ne détache pas le nom de la cible, et supprimerait
                // donc le « mirage » d'absence que ReadAllTextOrNull doit rattraper. Essayé
                // le 13/08/2026, et c'est PIRE : nos lecteurs ouvrent avec FileShare.Delete,
                // ce qui met la cible en « suppression différée » et rend son nom
                // inutilisable tant qu'un lecteur la tient. Sous charge, 450 écritures
                // ratées sur 750 tentatives, contre AUCUNE avec ReplaceFile — et une
                // écriture ratée perd l'état d'une commande, là où un mirage ne coûte
                // qu'une relecture.
                if (File.Exists(path))
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, path);

                return;
            }
            catch (IOException) when (essai < Essais)
            {
                Thread.Sleep(PauseMs);
            }
        }
    }

    /// <summary>
    /// Lit le fichier ; si absent mais qu'un .tmp complet traîne (crash entre écriture et
    /// échange), l'ignore.
    ///
    /// <b>La lecture s'efface devant l'écriture au lieu de lui disputer le fichier.</b>
    /// <c>File.ReadAllText</c> ouvre en <c>FileShare.Read</c>, ce qui interdit la
    /// suppression et fait donc échouer le <c>File.Replace</c> de
    /// <see cref="WriteAllText"/>. On ouvre ici en <c>ReadWrite | Delete</c> : l'échange
    /// peut avoir lieu pendant qu'on lit, et notre descripteur continue de rendre
    /// l'ANCIENNE version — entière, jamais un mélange des deux. C'est exactement ce que
    /// promet un remplacement atomique, et le lecteur n'a pas besoin de mieux.
    ///
    /// La reprise qui reste couvre le cas où la cible est indisponible au moment précis de
    /// l'ouverture. Vu à Arcueil le 13/08/2026, au tout premier tirage du poste : l'écran
    /// d'accueil comptait les agrandissements en attente pendant que l'impression écrivait
    /// <c>order.json</c>, et le compteur affichait 0.
    /// </summary>
    public static string? ReadAllTextOrNull(string path)
    {
        // LE CAS COURANT D'ABORD, ET SANS EXCEPTION : un fichier qui n'a jamais existé.
        // C'est celui de ResumePoint et SpoolState, sur le chemin d'impression, qui
        // interrogent des fichiers pas encore écrits. Sans ce raccourci, ils paient les
        // sept exceptions et les trois pauses de la reprise ci-dessous — 47 ms par appel
        // mesurés le 13/08/2026, contre quelques microsecondes ici.
        //
        // ⚠ LE .tmp EST LA CONDITION, et il n'est pas décoratif. Tester la seule absence de
        // la cible ramenait le mirage en force — 5 % des lectures sous charge, mesuré le
        // 13/08/2026 : pendant l'échange, `File.Exists` rend faux pour la cible AUSSI, et
        // le raccourci concluait « absent » alors qu'une écriture était en cours.
        //
        // Le .tmp, lui, n'existe QUE pendant qu'on écrit : sa présence dit « repasse par la
        // reprise patiente », son absence dit « ce fichier n'existe vraiment pas ».
        if (!File.Exists(path) && !File.Exists(path + ".tmp"))
        {
            // Un dernier regard après avoir cédé la main. Il reste une fenêtre étroite où
            // le .tmp vient d'être consommé et où la cible n'est pas encore reparue : les
            // deux tests ci-dessus y répondent faux alors que le fichier existe bel et
            // bien. Un `Yield` coûte quelques microsecondes et divise ce reliquat.
            Thread.Yield();
            if (!File.Exists(path)) return null;
        }

        var conflits = 0;
        var absences = 0;

        while (true)
        {
            try
            {
                // FileShare.Delete est la pièce maîtresse : sans lui, cette lecture
                // empêcherait l'échange d'en face.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);

                return reader.ReadToEnd();
            }
            // Un dossier absent est une condition stable : rien à attendre.
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            // L'ABSENCE PEUT ÊTRE UN MIRAGE, et le mirage ne se reconnaît pas en regardant
            // autour. Pendant l'échange, il existe une fenêtre où NI la cible NI le .tmp ne
            // portent de nom — mesuré, 24 fois sur 11 472 lectures : les deux
            // <c>File.Exists</c> rendaient faux, et le fichier était pourtant là l'instant
            // d'après. Un lecteur qui passe là rendait null, et <c>OrderFolderStore.Load</c>
            // rendant null, la commande disparaissait de <c>ScanRecent</c> — sans une ligne
            // au journal, puisque rien n'avait « échoué ».
            //
            // On cède donc la main au lieu d'attendre, et c'est délibéré : la fenêtre se
            // compte en microsecondes, tandis qu'un fichier VRAIMENT absent est le cas
            // courant de ResumePoint et SpoolState, sur le chemin d'impression. Trois
            // <c>Yield</c> leur coûtent quelques microsecondes ; trois pauses de 20 ms
            // auraient coûté un ralentissement à chaque tirage.
            catch (FileNotFoundException)
            {
                if (++absences > EssaisAbsence) return null;

                // Les premiers essais CÈDENT LA MAIN sans dormir : la fenêtre se compte en
                // microsecondes sur une machine au repos, et un fichier vraiment absent —
                // le cas courant de ResumePoint et SpoolState — ne doit rien payer.
                //
                // Les suivants dorment, parce que sur une machine CHARGÉE l'échange ne
                // reprend pas la main en trois cycles : mesuré sur la suite d'essais
                // complète, où huit fils tournent de front, un `Yield` seul laissait encore
                // passer des mirages. Cinq millisecondes suffisent, et seul un fichier
                // absent de peu les paie.
                if (absences <= EssaisSansAttendre) Thread.Yield();
                else Thread.Sleep(5);
            }
            // Conflit de partage : là il faut vraiment laisser passer l'autre.
            catch (IOException)
            {
                if (++conflits >= Essais) throw;
                Thread.Sleep(PauseMs);
            }
        }
    }
}
