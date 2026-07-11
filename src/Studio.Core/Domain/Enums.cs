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
}

public enum FitMode
{
    Fill, // « plein » : recadre pour remplir le format
    Fit,  // « entier » : image complète avec marges blanches
}
