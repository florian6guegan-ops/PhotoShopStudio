namespace Studio.Tests;

/// <summary>
/// Les classes d'essais qui touchent à l'état STATIQUE de <c>BiRefNetMatting</c> —
/// dossiers cherchés, modèle préféré, modèles écartés, session.
///
/// xUnit mène les classes d'essais de front par défaut. Deux d'entre elles qui écrivent
/// dans les mêmes champs statiques se marchent dessus, et le défaut qui en sort ne se
/// reproduit qu'une fois sur dix — le pire genre. Les rassembler dans une collection les
/// met en file indienne.
///
/// Chacune remet malgré tout ce qu'elle a trouvé dans son <c>Dispose</c> : la collection
/// évite le chevauchement, elle ne dispense pas de ranger derrière soi.
/// </summary>
[CollectionDefinition(Nom)]
public sealed class DetourageStatiqueCollection
{
    public const string Nom = "detourage-statique";
}
