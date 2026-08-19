using System;
using System.Collections.Generic;
using System.Linq;
using Studio.Core.Catalog;
using Studio.Core.Domain;

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
        Pour(commande, App.Services.Catalog, App.Services.Printer.PreferredMinilabMachine);

    /// <param name="machineImposee">
    /// La machine choisie par l'opérateur dans la barre de la grille, quand il en a choisi
    /// une. Elle prime sur celle du produit — c'est la règle de
    /// <c>PrintOrchestrator.MachineDemandee</c>, et la file doit lire la même chose, sans
    /// quoi deux commandes qui partent sur la même machine porteraient deux clés
    /// différentes et s'imprimeraient ensemble.
    /// </param>
    public static RessourceDImpression? Pour(
        Order commande, ProductCatalog catalogue, string? machineImposee)
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
                        FinitionDemandee(enveloppe));

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

    /// <summary>La ressource minilab, machine visée et finition comprises.</summary>
    private static RessourceDImpression Minilab(string? machine, string? finition)
    {
        var cle = $"minilab:{(string.IsNullOrWhiteSpace(machine) ? "auto" : machine.ToUpperInvariant())}" +
                  $":{(string.IsNullOrWhiteSpace(finition) ? "auto" : finition.ToLowerInvariant())}";

        var libelle = "minilab";
        if (!string.IsNullOrWhiteSpace(machine)) libelle += $" {machine.ToUpperInvariant()}";
        if (!string.IsNullOrWhiteSpace(finition)) libelle += $", finition {finition.ToLowerInvariant()}";

        return new RessourceDImpression(cle, libelle);
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
