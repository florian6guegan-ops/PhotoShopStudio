namespace Studio.Imaging.Geometry;

/// <summary>
/// Les 274 documents du référentiel, dits en FRANÇAIS.
///
/// <b>Pourquoi cette table existe.</b> Le référentiel vient de DiLand et parle anglais :
/// « Spain — Passport », « Germany — ID Card », « Japan — Visa ». L'écran « Autres
/// formats… » les affichait tels quels, et l'opérateur cherchait « Espagne » dans une liste
/// où le pays s'appelle « Spain » — sans compter que le client, lui, dit « Allemagne ».
/// Demandé le 18/08/2026.
///
/// <b>Une table écrite, et non une traduction automatique.</b> <c>RegionInfo</c> saurait
/// nommer la plupart de ces pays, mais dans la langue de WINDOWS et non dans la nôtre : un
/// poste installé en anglais aurait continué d'afficher « Spain ». Et surtout, le
/// référentiel contient ses propres fautes de frappe — « Andora », « Cameron »,
/// « Combodia », « Uzbeskistan », « Palestien » — qu'aucune table standard ne reconnaît.
/// Elles sont donc des CLÉS ici, telles qu'elles sont écrites dans le fichier : on traduit
/// ce que le référentiel dit, pas ce qu'il aurait dû dire.
///
/// <b>Un pays inconnu garde son nom.</b> Un référentiel corrigé ou complété par un poste
/// ne doit jamais faire disparaître une ligne de l'écran : au pire, elle reste en anglais.
/// </summary>
public static class TraductionIdentite
{
    /// <summary>Le pays, en français. Rendu tel quel s'il n'est pas dans la table.</summary>
    public static string Pays(string? anglais)
    {
        var cle = (anglais ?? "").Trim();
        return cle.Length > 0 && LesPays.TryGetValue(cle, out var fr) ? fr : cle;
    }

    /// <summary>Le type de document, en français. Rendu tel quel s'il n'est pas dans la table.</summary>
    public static string Document(string? anglais)
    {
        var cle = (anglais ?? "").Trim();
        return cle.Length > 0 && LesDocuments.TryGetValue(cle, out var fr) ? fr : cle;
    }

    /// <summary>
    /// Les types de documents. Le référentiel en compte onze écritures pour quatre
    /// notions : l'italien s'y est glissé (« Passaporto », « Patente nautica »), et le visa
    /// s'écrit de quatre façons dont une en capitales.
    /// </summary>
    private static readonly Dictionary<string, string> LesDocuments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Passport"] = "Passeport",
            ["Passaporto"] = "Passeport",
            ["ID Card"] = "Carte d'identité",
            ["Carta d'identità"] = "Carte d'identité",
            ["Visa"] = "Visa",
            ["VISA"] = "Visa",
            ["Visa (40x60)"] = "Visa (40 × 60 mm)",
            ["Visa (40x 60)"] = "Visa (40 × 60 mm)",
            ["Visa (2 x 2)"] = "Visa (2 × 2 pouces)",
            ["Patente"] = "Permis de conduire",
            ["Patente nautica"] = "Permis bateau",
        };

    /// <summary>
    /// Les pays, tels que le référentiel les écrit — fautes comprises.
    ///
    /// « Czech » et « Czech Republic » y figurent tous les deux, comme « Dutch_Holland » et
    /// « Nederland » : ce sont deux entrées distinctes du fichier, avec chacune leurs cotes,
    /// et les fondre ici ferait disparaître une norme de l'écran.
    /// </summary>
    private static readonly Dictionary<string, string> LesPays =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Afghanistan"] = "Afghanistan",
            ["Albania"] = "Albanie",
            ["Algeria"] = "Algérie",
            ["Andora"] = "Andorre",
            ["Angola"] = "Angola",
            ["Antigua"] = "Antigua-et-Barbuda",
            ["Argentina"] = "Argentine",
            ["Armenia"] = "Arménie",
            ["Aruba"] = "Aruba",
            ["Australia"] = "Australie",
            ["Austria"] = "Autriche",
            ["Azerbaijan"] = "Azerbaïdjan",
            ["Bahamas"] = "Bahamas",
            ["Bahrain"] = "Bahreïn",
            ["Bangladesh"] = "Bangladesh",
            ["Barbados"] = "Barbade",
            ["Belarus"] = "Biélorussie",
            ["Belgium"] = "Belgique",
            ["Belize"] = "Belize",
            ["Bermuda"] = "Bermudes",
            ["Bolivia"] = "Bolivie",
            ["Bosnia"] = "Bosnie-Herzégovine",
            ["Botswana"] = "Botswana",
            ["Brazil"] = "Brésil",
            ["Brunei"] = "Brunei",
            ["Bulgaria"] = "Bulgarie",
            ["Burma Myanmar"] = "Birmanie (Myanmar)",
            ["Burundi"] = "Burundi",
            ["Cameron"] = "Cameroun",
            ["Canada"] = "Canada",
            ["Cape Verde"] = "Cap-Vert",
            ["Chad"] = "Tchad",
            ["Chile"] = "Chili",
            ["China"] = "Chine",
            ["Colombia"] = "Colombie",
            ["Combodia"] = "Cambodge",
            ["Congo Republic"] = "République du Congo",
            ["Costa Rica"] = "Costa Rica",
            ["Croatia"] = "Croatie",
            ["Cuba"] = "Cuba",
            ["Cyprus"] = "Chypre",
            ["Czech"] = "Tchéquie",
            ["Czech Republic"] = "République tchèque",
            ["Denmark"] = "Danemark",
            ["Dominican Republic"] = "République dominicaine",
            ["Dutch_Holland"] = "Pays-Bas",
            ["Ecuador"] = "Équateur",
            ["Egypt"] = "Égypte",
            ["El Salvador"] = "Salvador",
            ["Eritrea"] = "Érythrée",
            ["Estonia"] = "Estonie",
            ["Ethiopia"] = "Éthiopie",
            ["Fiji"] = "Fidji",
            ["Finland"] = "Finlande",
            ["France"] = "France",
            ["French Guiana"] = "Guyane française",
            ["Gabon"] = "Gabon",
            ["Georgia Republic"] = "Géorgie",
            ["Germany"] = "Allemagne",
            ["Ghana"] = "Ghana",
            ["Greece"] = "Grèce",
            ["Greenland"] = "Groenland",
            ["Guam"] = "Guam",
            ["Guatemala"] = "Guatemala",
            ["Guyana"] = "Guyana",
            ["Haiti"] = "Haïti",
            ["Honduras"] = "Honduras",
            ["Hong Kong"] = "Hong Kong",
            ["Hungary"] = "Hongrie",
            ["Iceland"] = "Islande",
            ["India"] = "Inde",
            ["Indonesia"] = "Indonésie",
            ["Iran"] = "Iran",
            ["Iraq"] = "Irak",
            ["Ireland"] = "Irlande",
            ["Israel"] = "Israël",
            ["Italy"] = "Italie",
            ["Ivory Coast"] = "Côte d'Ivoire",
            ["Jamaica"] = "Jamaïque",
            ["Japan"] = "Japon",
            ["Jordan"] = "Jordanie",
            ["Kazakhstan"] = "Kazakhstan",
            ["Kenya"] = "Kenya",
            ["Korea"] = "Corée du Sud",
            ["Kuwait"] = "Koweït",
            ["Laos"] = "Laos",
            ["Latvia"] = "Lettonie",
            ["Lebanon"] = "Liban",
            ["Lesotho"] = "Lesotho",
            ["Liberia"] = "Liberia",
            ["Libya"] = "Libye",
            ["Liechenstein"] = "Liechtenstein",
            ["Lithuania"] = "Lituanie",
            ["Luxembourg"] = "Luxembourg",
            ["Macedonia"] = "Macédoine du Nord",
            ["Madagascar"] = "Madagascar",
            ["Malawi"] = "Malawi",
            ["Malaysia"] = "Malaisie",
            ["Mali"] = "Mali",
            ["Malta"] = "Malte",
            ["Mauritius"] = "Maurice",
            ["Mexico"] = "Mexique",
            ["Moldova"] = "Moldavie",
            ["Monaco"] = "Monaco",
            ["Mongolia"] = "Mongolie",
            ["Morocco"] = "Maroc",
            ["Mozambique"] = "Mozambique",
            ["Namibia"] = "Namibie",
            ["Nederland"] = "Pays-Bas",
            ["Nepal"] = "Népal",
            ["New Zealand"] = "Nouvelle-Zélande",
            ["Nicaragua"] = "Nicaragua",
            ["Niger"] = "Niger",
            ["Nigeria"] = "Nigeria",
            ["North Korea"] = "Corée du Nord",
            ["Norway"] = "Norvège",
            ["Oman"] = "Oman",
            ["Palestien"] = "Palestine",
            ["Panama"] = "Panama",
            ["Papa New Guinea"] = "Papouasie-Nouvelle-Guinée",
            ["Paraguay"] = "Paraguay",
            ["Peru"] = "Pérou",
            ["Philippines"] = "Philippines",
            ["Poland"] = "Pologne",
            ["Portugal"] = "Portugal",
            ["Puerto Rico"] = "Porto Rico",
            ["Romania"] = "Roumanie",
            ["Russia"] = "Russie",
            ["Rwanda"] = "Rwanda",
            ["Senegal"] = "Sénégal",
            ["Serbia"] = "Serbie",
            ["Singapore"] = "Singapour",
            ["Slovakia"] = "Slovaquie",
            ["Slovenia"] = "Slovénie",
            ["Solomon Islands"] = "Îles Salomon",
            ["Somalia"] = "Somalie",
            ["South Africa"] = "Afrique du Sud",
            ["Spain"] = "Espagne",
            ["Sri lanka"] = "Sri Lanka",
            ["Sudan"] = "Soudan",
            ["Suriname"] = "Suriname",
            ["Sweden"] = "Suède",
            ["Switzerland"] = "Suisse",
            ["Syria"] = "Syrie",
            ["Tanzania"] = "Tanzanie",
            ["Thailand"] = "Thaïlande",
            ["Tonga"] = "Tonga",
            ["Trinidad"] = "Trinité-et-Tobago",
            ["Tunisie"] = "Tunisie",
            ["Turkey"] = "Turquie",
            ["Uganda"] = "Ouganda",
            ["Ukraine"] = "Ukraine",
            ["United Kingdom"] = "Royaume-Uni",
            ["United States"] = "États-Unis",
            ["Uruguay"] = "Uruguay",
            ["Uzbeskistan"] = "Ouzbékistan",
            ["Venezuela"] = "Venezuela",
            ["Vietnam"] = "Viêt Nam",
            ["Virgin Islands"] = "Îles Vierges",
            ["Zambia"] = "Zambie",
            ["Zimbabwe"] = "Zimbabwe",
        };
}
