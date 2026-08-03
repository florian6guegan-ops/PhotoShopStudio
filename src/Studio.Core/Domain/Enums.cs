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

    /// <summary>
    /// Arrêtée par l'opérateur. Ce qui était parti au minilab lui a été rappelé ; ce qui
    /// était déjà sorti est sorti. Ajouté EN FIN d'énumération : les commandes déjà
    /// enregistrées portent des valeurs numériques qu'il ne faut pas déplacer.
    /// </summary>
    Canceled,
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

    /// <summary>
    /// Envoi direct au minilab Fuji par son SDK, via le relais 32 bits. Le DE100 n'écoute
    /// PAS le spouleur Windows : ses files y sont branchées sur le port « nul », qui avale
    /// les travaux sans rien imprimer.
    /// </summary>
    FujiMinilab,

    /// <summary>
    /// Rien n'est imprimé : la prestation est l'envoi des fichiers au client par courriel.
    ///
    /// C'est un vrai produit du catalogue, avec son prix, parce que c'est une vraie ligne
    /// de caisse — 5,00 € par photo, décision de l'exploitant du 03/08/2026. Le facturer
    /// hors commande le laisserait hors du ticket, hors du total et hors des statistiques.
    ///
    /// Ajouté EN FIN d'énumération : les commandes déjà enregistrées portent des valeurs
    /// numériques qu'il ne faut pas déplacer.
    /// </summary>
    Email,
}

public enum FitMode
{
    Fill, // « plein » : recadre pour remplir le format
    Fit,  // « entier » : image complète avec marges blanches

    /// <summary>
    /// « Polaroid » : la photo remplit une fenêtre presque carrée, posée dans un cadre blanc
    /// aux cotes du film 600 — marges fines sur trois côtés, large bande en bas.
    ///
    /// La teinte délavée et chaude du Polaroid vient AVEC : c'est une seule et même
    /// intention, et l'opérateur qui veut des couleurs franches choisit un tirage ordinaire.
    ///
    /// Ajouté EN FIN d'énumération : les commandes déjà enregistrées portent des valeurs
    /// numériques qu'il ne faut pas déplacer.
    /// </summary>
    Polaroid,
}
