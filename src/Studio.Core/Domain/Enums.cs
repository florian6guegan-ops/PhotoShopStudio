namespace Studio.Core.Domain;

public enum OrderStatus
{
    Draft,      // en cours de composition (borne ou opérateur)
    Submitted,  // envoyée par le client, pas encore vue
    InReview,   // ouverte par l'opérateur
    Printing,   // au moins une enveloppe en cours d'impression
    Ready,      // tout est imprimé, en attente de retrait
    Delivered,
    Cancelled,
}

public enum EnvelopeStatus
{
    Pending,
    Rendering,
    Spooled,   // job soumis au spouleur Windows — ne JAMAIS resoumettre automatiquement
    Printed,
    Error,
    /// <summary>
    /// Fichiers rendus, à imprimer à la main depuis Photoshop (grands formats sur l'Epson).
    /// Aucun travail n'a été soumis à Windows : c'est l'opérateur qui conclut.
    /// </summary>
    AwaitingManualPrint,
}

/// <summary>Par où sort un produit une fois rendu.</summary>
public enum ProductOutput
{
    /// <summary>Envoi direct à une file d'impression Windows.</summary>
    Printer,

    /// <summary>
    /// Rendu en fichier, sans passer par le spouleur : l'opérateur ouvre le fichier dans
    /// Photoshop et pilote lui-même l'imprimante. C'est le circuit des agrandissements
    /// au-delà du 21×29,7 sur l'Epson SC-P800.
    /// </summary>
    ManualFile,
}

public enum FitMode
{
    Fill, // « plein » : recadre pour remplir le format
    Fit,  // « entier » : image complète avec marges blanches
}
