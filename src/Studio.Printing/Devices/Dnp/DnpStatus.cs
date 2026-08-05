using System.Text.Json.Serialization;

namespace Studio.Printing.Devices.Dnp;

/// <summary>Famille à laquelle appartient un code d'état DNP.</summary>
public enum DnpStatusGroup
{
    Unknown = 0,
    /// <summary>Fonctionnement courant : repos, impression, fin de papier ou de ruban, refroidissement.</summary>
    Usual = 0x10000,
    /// <summary>Intervention opérateur attendue : capot, bourrage, ruban, bac à chutes.</summary>
    Setting = 0x20000,
    /// <summary>Panne matérielle : SAV.</summary>
    Hardware = 0x40000,
    /// <summary>Erreur système.</summary>
    System = 0x80000,
    /// <summary>Mise à jour du micrologiciel en cours.</summary>
    FirmwareUpdate = 0x100000,
}

/// <summary>
/// État d'une imprimante DNP, décodé depuis le mot renvoyé par <c>GetStatus</c>.
///
/// Le codage est un masque : les 16 bits hauts portent la famille (<see cref="DnpStatusGroup"/>),
/// les bits bas la condition précise, et le bit de poids fort (0x80000000) signale une
/// erreur de communication avec la DLL elle-même.
/// </summary>
public sealed record DnpStatus(uint Raw)
{
    private const uint ErrorFlag = 0x80000000u;
    private const uint TimeoutCode = 0x80000001u;

    // Tout ce qui suit se DÉDUIT de Raw, et ne traverse donc pas le tube du relais : le
    // protocole transporte la donnée brute, jamais ses interprétations. Ce n'est pas
    // qu'une question de taille de message — sérialiser « Group » obligeait le relais 32
    // bits à construire un convertisseur d'énumération par réflexion, ce dont son moteur
    // d'exécution mourait une fois sur deux (voir De100Protocol).

    /// <summary>La DLL n'a pas pu interroger l'imprimante (débranchée, éteinte, port occupé).</summary>
    [JsonIgnore]
    public bool IsCommunicationFailure => (Raw & ErrorFlag) != 0;

    /// <summary>L'imprimante n'a pas répondu dans le délai imparti.</summary>
    [JsonIgnore]
    public bool IsTimeout => Raw == TimeoutCode;

    /// <summary>Famille de l'état.</summary>
    [JsonIgnore]
    public DnpStatusGroup Group => IsCommunicationFailure
        ? DnpStatusGroup.Unknown
        : (Raw & 0xFFFF0000u) switch
        {
            (uint)DnpStatusGroup.Usual => DnpStatusGroup.Usual,
            (uint)DnpStatusGroup.Setting => DnpStatusGroup.Setting,
            (uint)DnpStatusGroup.Hardware => DnpStatusGroup.Hardware,
            (uint)DnpStatusGroup.System => DnpStatusGroup.System,
            (uint)DnpStatusGroup.FirmwareUpdate => DnpStatusGroup.FirmwareUpdate,
            _ => DnpStatusGroup.Unknown,
        };

    /// <summary>Vrai si l'imprimante peut accepter un tirage maintenant.</summary>
    [JsonIgnore]
    public bool IsReady => Raw is Codes.UsualIdle or Codes.UsualStandstill;

    /// <summary>Vrai si l'imprimante travaille (impression ou refroidissement) : elle repartira seule.</summary>
    [JsonIgnore]
    public bool IsBusy => Raw is Codes.UsualPrinting or Codes.UsualCooling or Codes.UsualMotorCooling;

    /// <summary>Vrai si un opérateur doit intervenir sur la machine.</summary>
    [JsonIgnore]
    public bool NeedsOperator => Raw is Codes.UsualPaperEnd or Codes.UsualRibbonEnd
        || Group is DnpStatusGroup.Setting;

    /// <summary>Vrai si la machine est en panne et relève du SAV.</summary>
    [JsonIgnore]
    public bool IsFault => Group is DnpStatusGroup.Hardware or DnpStatusGroup.System;

    /// <summary>Message destiné à l'opérateur de la borne.</summary>
    [JsonIgnore]
    public string Message => Raw switch
    {
        TimeoutCode => "L'imprimante ne répond pas (délai dépassé).",
        Codes.UsualIdle => "Prête.",
        Codes.UsualPrinting => "Impression en cours.",
        Codes.UsualStandstill => "En veille, prête à imprimer.",
        Codes.UsualPaperEnd => "Papier épuisé : changer le rouleau.",
        Codes.UsualRibbonEnd => "Ruban épuisé : changer la cassette.",
        Codes.UsualCooling => "Refroidissement de la tête en cours.",
        Codes.UsualMotorCooling => "Refroidissement du moteur en cours.",
        Codes.SettingCoverOpen => "Capot ouvert : le refermer.",
        Codes.SettingPaperJam => "Bourrage papier : dégager le chemin papier.",
        Codes.SettingRibbonError => "Problème de ruban : vérifier la cassette.",
        Codes.SettingPaperError => "Problème de papier : vérifier le rouleau.",
        Codes.SettingDataError => "Données de tirage rejetées par l'imprimante.",
        Codes.SettingScrapBoxError => "Bac à chutes plein ou mal en place.",
        Codes.SystemError01 => "Erreur système de l'imprimante : redémarrer la machine.",
        _ when IsCommunicationFailure => "Imprimante injoignable (éteinte, débranchée ou port occupé).",
        _ when Group is DnpStatusGroup.Hardware => $"Panne matérielle de l'imprimante (code 0x{Raw:X8}) : contacter le SAV.",
        _ when Group is DnpStatusGroup.FirmwareUpdate => "Mise à jour du micrologiciel en cours : ne pas éteindre.",
        _ => $"État inconnu (0x{Raw:X8}).",
    };

    /// <summary>Codes bruts renvoyés par <c>GetStatus</c>.</summary>
    public static class Codes
    {
        public const uint UsualIdle = 65537;
        public const uint UsualPrinting = 65538;
        public const uint UsualStandstill = 65540;
        public const uint UsualPaperEnd = 65544;
        public const uint UsualRibbonEnd = 65552;
        public const uint UsualCooling = 65568;
        public const uint UsualMotorCooling = 65600;

        public const uint SettingCoverOpen = 131073;
        public const uint SettingPaperJam = 131074;
        public const uint SettingRibbonError = 131076;
        public const uint SettingPaperError = 131080;
        public const uint SettingDataError = 131088;
        public const uint SettingScrapBoxError = 131104;

        public const uint HardwareError01 = 262145;
        public const uint HardwareError02 = 262146;
        public const uint HardwareError03 = 262148;
        public const uint HardwareError04 = 262152;
        public const uint HardwareError05 = 262160;
        public const uint HardwareError06 = 262176;
        public const uint HardwareError07 = 262208;
        public const uint HardwareError08 = 262272;
        public const uint HardwareError09 = 262400;
        public const uint HardwareError10 = 262656;

        public const uint SystemError01 = 524289;

        public const uint FirmwareIdle = 1048577;
        public const uint FirmwareWriting = 1048578;
        public const uint FirmwareFinished = 1048580;
        public const uint FirmwareDataError = 1048584;
        public const uint FirmwareDeviceError = 1048592;
        public const uint FirmwareOtherError = 1048608;

        public const uint CommunicationTimeout = 0x80000001u;
    }
}
