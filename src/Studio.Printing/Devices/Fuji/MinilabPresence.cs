namespace Studio.Printing.Devices.Fuji;

/// <summary>
/// Les minilabs que le spouleur Windows connaît, quand le relais n'en découvre aucun.
///
/// <b>Une machine éteinte doit rester visible.</b> Le bandeau tenait les Fuji en mémoire de
/// session : tant que le relais avait répondu au moins une fois, une panne passagère ne
/// les faisait pas disparaître. Mais sur une application qui DÉMARRE machines éteintes,
/// cette mémoire est vide, et le bandeau n'affichait rien du tout — comme si le poste
/// n'avait pas de minilab. C'est ce qu'a vu le poste de Créteil le 07/08/2026, là où
/// Maisons-Alfort les montrait parce que son application tournait depuis qu'elles étaient
/// allumées.
///
/// Les DNP avaient déjà ce filet (voir <c>DiLandPresence.VuesParWindows</c>) ; les Fuji
/// non. C'est la même idée : le spouleur répond toujours, lui.
///
/// <b>Ce qu'on affirme est mince, et c'est voulu.</b> La file Windows prouve que la
/// machine est INSTALLÉE sur ce poste, rien de plus — le DE100 n'imprime d'ailleurs pas
/// par le spouleur, ses files sont branchées sur le port « nul ». On rend donc une machine
/// « hors ligne » sans média, sans compteur et sans format : tout cela viendra du relais
/// dès qu'elle répondra, et remplacera ces tuiles.
/// </summary>
public static class MinilabPresence
{
    /// <summary>
    /// Reconnu sur le MODÈLE et non sur la marque : « FUJIFILM DE100 », « FUJIFILM
    /// DE100-2 », mais aussi une file renommée qui garde le modèle dans son nom.
    /// </summary>
    public static bool EstUnMinilab(string nomDeFile) =>
        nomDeFile.Contains("DE100", StringComparison.OrdinalIgnoreCase);

    /// <param name="delai">Au-delà, on renonce : une liste d'imprimantes ne vaut pas une attente.</param>
    public static IReadOnlyList<De100PrinterInfo> VusParWindows(TimeSpan? delai = null)
    {
        var budget = delai ?? TimeSpan.FromSeconds(3);

        var lecture = Task.Run(() =>
        {
            var trouves = new List<De100PrinterInfo>();

            var files = System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Where(EstUnMinilab)
                .OrderBy(nom => nom, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < files.Count; i++)
            {
                // L'identifiant est DÉDUIT de l'ordre des files : « FUJIFILM DE100 » puis
                // « FUJIFILM DE100-2 » donnent A puis B, ce qui est la numérotation du
                // DE100. Rien ne le garantit tant que la machine n'a pas parlé — mais une
                // tuile hors ligne ne sert qu'à dire « elle est là, elle dort », et dès
                // qu'elle répond c'est le relais qui fait foi.
                trouves.Add(new De100PrinterInfo(
                    MachineId: (char)('A' + i),
                    Status: De100PrinterStatus.Offline,
                    RegistrationNumber: "",
                    Model: files[i],
                    SerialNumber: "",
                    IpAddress: "",
                    TotalPrintCount: 0,
                    Media: null,
                    Supplies: null,
                    Formats: []));
            }

            return (IReadOnlyList<De100PrinterInfo>)trouves;
        });

        try
        {
            return lecture.Wait(budget) ? lecture.Result : [];
        }
        catch (Exception)
        {
            // une file qui ne répond pas ne doit pas empêcher le bandeau de s'afficher
            return [];
        }
    }
}
