using System.Windows;
using System.Windows.Input;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le curseur d'attente de l'application : le diaphragme du logo qui tourne.
///
/// Il remplace <see cref="Cursors.Wait"/> partout où l'écran fait patienter. Le cercle
/// bleu de Windows disait « le système travaille » ; celui-ci dit « Studio travaille »,
/// et c'est ce qu'on veut voir sur un poste où trois logiciels tournent en même temps.
///
/// <b>Chargé une seule fois.</b> Un curseur animé se relit entièrement à chaque
/// construction — douze bitmaps — et ces attentes-là sont justement les moments où l'on
/// n'a rien à perdre en travail inutile.
///
/// <b>Il retombe sur le sablier du système.</b> Un curseur qui ne se charge pas ne doit
/// jamais empêcher une impression : mieux vaut le cercle bleu que rien du tout.
/// </summary>
public static class CurseurStudio
{
    private static readonly Lazy<Cursor> Charge = new(() =>
    {
        try
        {
            var flux = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/studio-attente.ani"))?.Stream;

            return flux is null ? Cursors.Wait : new Cursor(flux);
        }
        catch (Exception ex)
        {
            FileLog.Write("Curseur d'attente illisible : on garde celui du système", ex);
            return Cursors.Wait;
        }
    });

    /// <summary>Le curseur à poser sur <see cref="Mouse.OverrideCursor"/> pendant une attente.</summary>
    public static Cursor Attente => Charge.Value;
}
