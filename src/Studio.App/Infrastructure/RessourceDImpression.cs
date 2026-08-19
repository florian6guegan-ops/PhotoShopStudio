using System;
using System.Collections.Generic;
using System.Linq;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;

namespace Studio.App.Infrastructure;

/// <summary>
/// La machine qu'une commande VISE, telle qu'on peut la connaître avant tout rendu.
///
/// <b>Pourquoi une clé plutôt qu'une lettre de machine.</b> La machine réelle n'est
/// choisie qu'au moment de l'envoi, une fois les pages rendues : c'est le rouleau chargé
/// qui tranche (voir <c>PrintOrchestrator.ChoisirMachineEtRouleau</c>), et le demander
/// d'avance obligerait à interroger le relais 32 bits sur le fil de l'interface, au clic
/// même de l'opérateur. On raisonne donc sur ce que la commande DEMANDE — un minilab en
/// finition brillante, la DS620 — et non sur ce qu'elle obtiendra.
///
/// Deux commandes qui demandent la même chose passent l'une après l'autre. C'est la règle
/// voulue en boutique : une commande de tirages brillants n'est pas détournée vers la
/// machine d'à côté sous prétexte qu'elle est libre — elle attend son rouleau.
///
/// <b>La finition N'EST PAS une seconde dimension : c'est la machine, dite autrement.</b>
/// Le brillant est monté sur la DE100, le lustré sur la DE100-2 — c'est ainsi que la
/// boutique travaille, et DiLand le dit de la même façon (voir <see cref="FinitionPapier"/>,
/// où le relevé du 11/08/2026 ne montre aucun croisement). Demander du brillant, c'est donc
/// demander la machine A ; imposer la machine A, c'est demander du brillant. Tant que la clé
/// portait les deux séparément, <c>minilab:auto:brillant</c> et <c>minilab:A:auto</c>
/// passaient pour deux machines différentes et ne s'attendaient pas — la commande 19-036 est
/// ainsi partie le 19/08/2026 pendant que la 19-035 tirait encore.
/// </summary>
/// <param name="Cle">Ce qui décide de la file : deux commandes de même clé s'attendent.</param>
/// <param name="Libelle">Ce que l'opérateur lit dans le bandeau : « minilab, finition brillante ».</param>
public sealed record RessourceDImpression(string Cle, string Libelle)
{
    /// <summary>
    /// Ce que cette commande va occuper, ou null quand elle n'occupe rien de partagé.
    ///
    /// Null pour les agrandissements (tirés à la main sur l'Epson, hors file) et pour
    /// l'envoi par courriel : les mettre en file ferait attendre un travail qui ne touche
    /// aucune machine.
    /// </summary>
    public static RessourceDImpression? Pour(Order commande) =>
        Pour(commande, App.Services.Catalog, App.Services.Printer.PreferredMinilabMachine,
             App.Services.Minilab.DernierInstantane?.Etats);

    /// <param name="machineImposee">
    /// La machine choisie par l'opérateur dans la barre de la grille, quand il en a choisi
    /// une. Elle prime sur celle du produit — c'est la règle de
    /// <c>PrintOrchestrator.MachineDemandee</c>, et la file doit lire la même chose, sans
    /// quoi deux commandes qui partent sur la même machine porteraient deux clés
    /// différentes et s'imprimeraient ensemble.
    /// </param>
    /// <param name="etatsConnus">
    /// Le dernier état des machines rendu par le relais — celui que la barre du bas relit
    /// en boucle, donc jamais bien vieux. Il sert à savoir QUELLE machine porte la finition
    /// demandée, sans repasser par le relais au clic de l'opérateur. Null ou vide : on
    /// retombe sur le rangement habituel de la boutique, brillant en A et lustré en B.
    /// </param>
    public static RessourceDImpression? Pour(
        Order commande, ProductCatalog catalogue, string? machineImposee,
        IReadOnlyList<De100PrinterInfo>? etatsConnus = null)
    {
        ArgumentNullException.ThrowIfNull(commande);
        ArgumentNullException.ThrowIfNull(catalogue);

        foreach (var enveloppe in commande.Envelopes)
        {
            // une enveloppe déjà sortie n'occupe plus rien
            if (enveloppe.Status is EnvelopeStatus.Printed or EnvelopeStatus.Canceled) continue;

            var produits = enveloppe.Lines
                .Select(l => catalogue.Find(l.ProductCode))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();

            if (produits.Count == 0) continue;

            var produit = produits[0];

            switch (produit.Output)
            {
                case ProductOutput.FujiMinilab:
                    return Minilab(
                        string.IsNullOrWhiteSpace(machineImposee) ? produit.MinilabMachineId : machineImposee,
                        FinitionDemandee(enveloppe),
                        etatsConnus);

                case ProductOutput.Printer:
                    var file = produit.PrinterName;
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    return new RessourceDImpression(
                        $"spouleur:{file.ToLowerInvariant()}", file);

                // agrandissements et courriel : aucune machine partagée n'est prise
                default:
                    continue;
            }
        }

        return null;
    }

    /// <summary>
    /// Vrai quand deux commandes peuvent tomber sur la MÊME machine — donc doivent
    /// s'attendre.
    ///
    /// <b>Pourquoi ce n'est pas une simple égalité de clés.</b> Une machine nommée en
    /// désigne une, « auto » n'en désigne aucune : personne n'a tranché, et c'est le
    /// rouleau chargé qui décidera au moment de l'envoi. Ce peut donc très bien être celle
    /// que l'autre commande occupe. Dans le doute on attend : deux paquets mélangés dans le
    /// bac coûtent plus cher qu'une minute de patience.
    ///
    /// Deux machines NOMMÉES et différentes, en revanche, ce sont deux DE100 côte à côte :
    /// elles tirent en même temps, et c'est tout l'intérêt d'en avoir deux.
    /// </summary>
    public static bool Croisent(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var ga = a.Split(':');
        var gb = b.Split(':');

        // Le spouleur n'a qu'un nom de file, et Windows fait déjà la queue derrière : hors
        // du minilab, seule l'égalité stricte compte.
        if (ga.Length != 2 || gb.Length != 2) return false;
        if (!ga[0].Equals("minilab", StringComparison.OrdinalIgnoreCase)) return false;
        if (!gb[0].Equals("minilab", StringComparison.OrdinalIgnoreCase)) return false;

        return ga[1].Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               gb[1].Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// La ressource minilab : une machine, et rien d'autre.
    ///
    /// La finition n'entre pas dans la clé — elle y est déjà, traduite en machine par
    /// <see cref="MachineDeLaFinition"/>. Elle reste dans le libellé, parce que c'est le
    /// mot que l'opérateur a en tête et sur le ticket du client.
    /// </summary>
    private static RessourceDImpression Minilab(
        string? machine, string? finition, IReadOnlyList<De100PrinterInfo>? etatsConnus)
    {
        // Une machine imposée — barre de la grille, ou produit — ne se discute pas : c'est
        // la règle de PrintOrchestrator.MachineDemandee, où le rouleau ne décide plus rien.
        // La finition ne sert donc qu'à défaut.
        var visee = string.IsNullOrWhiteSpace(machine)
            ? MachineDeLaFinition(finition, etatsConnus)
            : machine.ToUpperInvariant();

        var libelle = "minilab";
        if (!string.IsNullOrWhiteSpace(visee)) libelle += $" {visee}";
        if (!string.IsNullOrWhiteSpace(finition)) libelle += $", finition {finition.ToLowerInvariant()}";

        return new RessourceDImpression($"minilab:{visee ?? "auto"}", libelle);
    }

    /// <summary>
    /// La machine qui porte cette finition, ou null quand la commande n'en demande aucune.
    ///
    /// <b>Relevée sur les machines, pas décrétée.</b> C'est le dernier état rendu par le
    /// relais qui dit quel rouleau est monté où — le même que lit la barre du bas. Un
    /// rouleau déplacé d'une machine à l'autre suit donc tout seul, sans que personne ait à
    /// le déclarer.
    ///
    /// Le rangement de la boutique ne sert que de dernier recours, quand le relais n'a
    /// encore rien dit : brillant en A, lustré en B. Se tromper ici ne fait pas partir un
    /// tirage sur le mauvais rouleau — la machine est revérifiée à l'envoi, dans
    /// <c>ChoisirMachineEtRouleau</c> — ça fait seulement attendre une commande qui aurait
    /// pu partir, ou partir deux commandes qui auraient pu attendre.
    /// </summary>
    public static string? MachineDeLaFinition(
        string? finition, IReadOnlyList<De100PrinterInfo>? etatsConnus = null)
    {
        var voulue = PrintOrchestrator.FinitionMinilab(finition);
        if (voulue is null) return null;

        var porteuse = etatsConnus?.FirstOrDefault(e => e.Media?.Surface == voulue);
        if (porteuse is not null) return porteuse.MachineId.ToString().ToUpperInvariant();

        return voulue switch
        {
            De100Surface.Glossy => "A",
            De100Surface.Lustre => "B",
            _ => null,
        };
    }

    /// <summary>
    /// La finition que les photos de l'enveloppe demandent, ou null si personne n'a
    /// tranché — le rouleau chargé décidera alors, comme aujourd'hui.
    /// </summary>
    private static string? FinitionDemandee(Envelope enveloppe) =>
        enveloppe.Lines
            .SelectMany(l => l.Items)
            .Select(i => i.Finish)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));

    /// <summary>Tirages que la commande va sortir, exemplaires compris.</summary>
    public static int TiragesDe(Order commande)
    {
        ArgumentNullException.ThrowIfNull(commande);

        return commande.Envelopes
            .Where(e => e.Status is not (EnvelopeStatus.Printed or EnvelopeStatus.Canceled))
            .SelectMany(e => e.Lines)
            .Sum(l => l.IsCustomSheet ? Math.Max(1, l.SheetCount) : l.TotalPrints);
    }
}
