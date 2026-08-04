# Notes d'architecture — à lire avant de retoucher ces parties

Ce fichier ne décrit pas tout le projet : il note les règles qu'on ne devine pas en lisant le
code, et qu'on casse sans s'en apercevoir.

---

## Vignettes : `ThumbnailService` travaille par paliers

Les seules tailles jamais produites sont `360, 512, 720, 1024, 1440, 2048`.

`GetJpeg(chemin, boite)` rend une vignette **d'au moins** `boite` de côté : il arrondit au
palier supérieur, et **reprend n'importe quel palier déjà en cache qui soit ≥ à la demande**.

**Pourquoi.** Chaque appelant demandait sa taille au pixel près et n'atteignait donc jamais le
cache d'un autre. La planche d'index réclamait ~219 px là où la planche-contact venait d'écrire
du 360 : vingt-neuf fichiers redécodés pour rien, devant le client.

**`ThumbnailService.Defaut` (512) et `IndexSheet.VignetteMaximalePx` (512) doivent rester
ÉGAUX.** C'est le seul point qui garantit que la planche d'index retombe toujours sur le cache
de la grille. Quand ils valaient 360 et « la taille de la cellule », un 30×40 réclamait 751 px,
montait au palier 1024, et redécodait les trente-six fichiers de 39 Mpx : 5 109 ms sur les 6,3 s
du rendu. 512 donne encore ~206 ppp imprimés sur la plus grande cellule rencontrée (63 mm) —
largement assez pour une planche où le client coche un numéro.

**Conséquence à connaître.** Un nouvel appelant ne doit PAS demander une taille arbitraire en
espérant l'obtenir exactement — il recevra le palier au-dessus. S'il lui faut une taille précise,
qu'il redimensionne lui-même après coup.

Vérifié par `tests/Studio.Tests/ThumbnailCacheTests.cs`.

---

## Chargement de la planche-contact : parallèle, par tranches ordonnées

`PhotoGridView.ChargerLesVignettesAsync` lit les vignettes **sur tous les cœurs**, par tranches
de `ProcessorCount * 2`, chaque tranche étant posée dans l'ordre de la planche.

- **Parallèle** parce que le séquentiel coûtait 5 594 ms sur 36 photos de 39 Mpx, contre 2 029 ms
  ici — et parce que c'est ce cache que la planche d'index vient reprendre.
- **Par tranches** parce qu'un lot unique laisserait la grille vide jusqu'au bout : jusqu'à 1 200
  photos peuvent être affichées (`PhotoScanner.MaxAffichable`).
- **Une seule ouverture par photo** : la définition venait d'un appel séparé à
  `ImagePipeline.GetOrientedSize`, soit un second parcours du fichier pour une information lue
  en même temps que la vignette.

---

## Planche d'index : tout ce qui est parallélisable l'est avant la composition

`IndexSheet.Render` procède en trois temps :

1. **rapports** — l'appelant passe les rapports largeur/hauteur qu'il connaît déjà
   (`Request.Aspects`, `0` = inconnu). Seuls les inconnus sont « pingés », en parallèle.
2. **préchargement** — lecture, décodage ET redimensionnement des vignettes, en parallèle, à la
   taille exacte de leur place. Toutes les cellules ayant la même taille, cela vaut pour toutes
   les planches d'un coup.
3. **composition** — séquentielle, car il n'y a qu'une image cible. Elle ne coûte plus qu'une
   recopie.

`Result.Thumbnails` porte la vignette d'affichage de chaque planche, réduite **sur place** après
écriture : l'appelant ne doit jamais relire le fichier qu'on vient d'écrire pour l'afficher.

Côté `PhotoGridView`, les rapports viennent de `PhotoItem.SourceAspect`, mais **seulement si
`SourceSizeKnown`** — sans quoi le rapport vaut 1 et la grille serait taillée pour des photos
carrées imaginaires.

Mesuré sur 29 photos de 6 Mpx, cache chaud : **~550 ms**, contre plusieurs secondes avant.

---

## `PhotoGridView._photos` est une `List`, pas une `ObservableCollection`

Toute insertion ou suppression doit être suivie de :

```csharp
PhotosGrid.ItemsSource = null;
PhotosGrid.ItemsSource = _photos;
```

Sans cela, **rien n'apparaît à l'écran** — c'est ainsi que la planche d'index était produite,
écrite sur le disque, et jamais montrée. `Ecarter` et `OnSort` le font déjà ; `OnIndexSheet` et
`RetirerLesPlanchesIndex` aussi.

Le bouton « Planche index » est une **bascule** : `_planchesIndex` mémorise ce qu'il a produit,
et un second appui retire au lieu de refabriquer.

---

## Gestion des couleurs des agrandissements

`LargeFormatPrinter.Print` convertit l'image **avant** de l'envoyer, via `IccTransform`, dès que
`ColorHandling == ApplicationManagesColor` et qu'un profil est renseigné.

Trois règles qui coûtent cher à réapprendre :

1. **`LargeFormatPrintSettings.PrinterProfile` est un CHEMIN COMPLET**, pas un nom de fichier.
   Les profils viennent de `catalog/icc` *et* du dossier couleur de Windows
   (`IccProfiles.Available`) — c'est là que le pilote Epson pose les treize profils SC-P800, et
   `catalog/icc` n'existe sur aucun poste tant qu'on n'y a rien importé.

2. **La compensation du point noir passe par `MagickImage.BlackPointCompensation`**, PAS par
   l'artefact `profile:black-point-compensation`. Cet artefact est accepté sans erreur et n'a
   strictement aucun effet — constaté sur les pixels avec le profil *SC-P800 Canvas Matte*.

3. **Le mode de rendu et la compensation du point noir ne se vérifient qu'avec un profil à table
   de correspondance.** Entre deux profils matriciels (sRGB, AdobeRGB…), ils ne changent
   légitimement rien : même point blanc, noir à zéro des deux côtés. Les tests utilisent donc un
   vrai profil SC-P800 installé par le pilote.

4. **`ColorTransformMode.Quantum`, jamais `HighRes`.** Mesuré sur 39 Mpx avec le profil Premium
   Luster : HighRes met **13 536 ms**, Quantum **1 459 ms**, pour un écart maximal de DEUX
   niveaux sur 255 — sur une photo réelle comme sur un dégradé noir-blanc. C'est ce seul appel
   qui rendait « Imprimer » interminable. Ne pas y revenir « pour la qualité » : la mesure est
   dans `IccTransform`.

---

## Le rendu des tirages : quatre mesures, quatre corrections

**C'était là que « Imprimer » était interminable, et non à l'impression.** Le parcours des
agrandissements a deux boutons « Imprimer » qui se confondent dans le ressenti : celui de la
GRILLE (et de « Modifier ») fabrique les fichiers, celui de la BOÎTE les relit et les
spoule. C'est le premier qui coûtait ; le second était déjà court.

Mesuré le 02/08/2026, deux photos réelles d'une commande en 40×50 :
**16,6 s → 4,5 s.** Quatre corrections indépendantes, dans l'ordre de leur poids.

### 1. Le format : JPEG pour les agrandissements

**Tout passe par `MagickInit.Write`, jamais par `image.Write`.** Le format découle de
l'extension, et `PrintOrchestrator.Extension` la choisit.

Sur un 40×50 à 300 ppp (4724 × 5906 = 27,9 Mpx) :

| Écriture | Durée | Taille |
|---|---|---|
| PNG par défaut | 15 228 ms | 14,7 Mo |
| PNG compression 1 | 12 531 ms | 16,3 Mo |
| PNG compression 0, sans filtre | 11 905 ms | 26,6 Mo |
| **JPEG qualité 95** | **694 ms** | **8,9 Mo** |

Le niveau de compression ne change presque rien : **l'encodeur PNG de Magick.NET est lent en
lui-même** sur ces définitions, indépendamment de zlib. Seul le changement de format règle la
question. Le niveau 1 reste posé pour les PNG qui subsistent — il vaut mieux que le défaut
sur les images qui compressent mal (32 s → 4 s sur un scan granuleux en 50×70).

**Le PNG reste pour les planches et pour le minilab.** Les planches portent des contours de
découpe de deux dixièmes de millimètre et de la date en petits caractères, autour desquels le
JPEG laisse des franges. Le minilab, lui, reçoit le fichier tel quel par le SDK Fuji : ses
formats acceptés ne se vérifient pas depuis un poste de développement, et ses rendus sont
petits (un 10×15 fait 2 Mpx) — il n'y a rien à y gagner.

### 2. Le rendu des photos d'une ligne est PARALLÈLE

Magick.NET est livré **sans OpenMP** sur ce poste : `ResourceLimits.Thread` vaut 1 et refuse
d'être changé. Un rendu occupe donc un cœur sur les huit, et les enchaîner laissait la
machine à 12 % pendant que le client attendait. Deux photos : 8,3 s → 4,5 s.

Bridé à **quatre** fils : un rendu d'agrandissement tient une cinquantaine de mégapixels en
mémoire, et ImageMagick bascule sur le disque au-delà de son budget de 2 Go.

Deux points à ne pas défaire : les pages sont rangées **à l'indice de leur photo** et non
dans l'ordre où les fils finissent ; et `Parallel.For` emballe les erreurs dans une
`AggregateException`, qu'on déballe pour que l'opérateur lise « fichier illisible : 003.jpg »
et non « une ou plusieurs erreurs se sont produites ».

### 3. « Remplir le format » découpe AVANT d'agrandir

`ImagePipeline.RenderInto`, mode `Fill` : on rogne d'abord la zone utile dans la source, on
agrandit ensuite. L'ordre inverse — `Resize(FillArea)` puis `Crop` — était le plus court à
écrire et le plus cher à exécuter : pour un 40×50 tiré d'une photo de 3024 × 2005, il
fabriquait une image de 8906 × 5906 (52,6 Mpx) dont il jetait 47 %.
**3 926 ms → 2 015 ms.** Le résultat est le même au demi-pixel de bord près.

### 4. Ce qui reste

| Étape | Durée sur un 40×50 |
|---|---|
| lecture de la source | 126 ms |
| redimensionnement | ~2 000 ms |
| corrections (contraste auto) | 532 ms |
| écriture JPEG | 689 ms |

Le redimensionnement domine et n'est pas réductible sans changer de filtre (Triangle ne
gagne que 7 % et adoucit). Les corrections sont appliquées **après** l'agrandissement : les
faire avant serait moins cher sur un agrandissement, mais Clarté et Netteté sont des
opérations à rayon en pixels — leur effet visuel changerait d'échelle.

---

## Agrandissements : la résolution d'ENVOI, et pas celle du périphérique

`LargeFormatPrinter.Print` procède dans cet ordre, et il ne doit pas changer :

**décoder → RÉDUIRE → convertir ICC → remettre au spouleur.**

La réduction vient en premier pour que la conversion ICC porte sur moins de pixels.

**Ne jamais remettre `InterpolationMode.HighQualityBicubic` sur le contexte de
l'imprimante.** Avec une interpolation de qualité demandée, GDI+ cesse de déléguer au pilote
(`StretchDIBits`) et rééchantillonne LUI-MÊME à la définition du périphérique. La SC-P800
annonce 1440 ppp : un 50×70 devient 28 346 × 39 685 px, soit **1,1 milliard de pixels**
fabriqués en mémoire puis spoulés. C'est ce seul réglage qui rendait « Imprimer »
interminable une fois `IccTransform` corrigé. Le rééchantillonnage de qualité a lieu en
amont, dans `MettreALEchelleDEnvoi`, sur un bitmap mémoire où il ne coûte presque rien.

`TailleDEnvoi` plafonne à **360 ppp** (`PppEnvoiMaximal`) — la résolution à laquelle les
Epson consomment les données, la montée à 1440 étant le travail du pilote — et **ne fait que
réduire** : fabriquer des pixels absents de la source coûterait le même prix pour rien.

La résolution annoncée suit **l'arrondi de la largeur retenue**, pas le facteur théorique :
c'est ce qui conserve le placement au millimètre. Vérifié par
`tests/Studio.Tests/LargeFormatScalingTests.cs`.

`LargeFormatPrinter.Log` est branché sur `FileLog` dans `AppServices.Load` — il ne l'était
pas, et le journal des durées partait dans le vide.

---

## Format « personnalisé » : le papier est un vrai produit, la taille ne l'est pas

Une planche à taille libre traverse le logiciel sous les traits d'un tirage ordinaire :

| | Ce que c'est |
|---|---|
| `OrderLine.ProductCode` | le **papier retenu** (`13x18`…), un vrai produit du catalogue |
| `OrderLine.CustomCell*Mm` | la taille des cases ; non nulle = la ligne est une planche |
| `OrderLine.SheetCount` | le nombre de planches, **figé à la commande** |
| `OrderLine.Items` | les photos, chacune avec son nombre de cases |

**Pourquoi le papier et non un produit inventé.** Tout le circuit minilab — `EnsurePaperFits`,
`MinilabPrintSize`, `FitPageToRoll`, `ChooseMinilabMachine` — interroge le produit de la
ligne. Un code fantôme le ferait échouer à `_catalog.Require`.

**`SheetCount` ne se recalcule jamais.** Il fixe le prix (facturation **au papier** : une
planche 13×18 coûte un tirage 13×18, quel qu'en soit le contenu). Le recalculer exposerait
une commande déjà annoncée au client à changer de prix parce que le catalogue a bougé. En
revanche la CAPACITÉ et l'orientation de la case sont recalculées au rendu, par
`CustomSheetLayout.CapacityOf` : le calcul est déterministe et ne vit qu'à un endroit.

**Le papier se choisit au PRIX, pas à la surface.** Décision de l'exploitant, 02/08/2026.
Pour des photos de 5,5 × 8 cm : 1 → un 8×10 (0,60 €) ; 2 → un 10×15 (0,60 €) ; **4 → DEUX
10×15 (1,20 €) et non un 13×18 (1,50 €)**, bien que le 13×18 tienne en une planche et
consomme moins de papier. Le magasin ne vend pas de la surface, il vend des tirages — et deux
petits coûtent souvent moins qu'un grand. La règle précédente privilégiait la planche unique
et coûtait trente centimes de trop à chaque commande de quatre.

Départages : prix le plus bas, puis le moins de planches, puis le plus petit papier. Un
catalogue sans prix retombe sur la surface, faute de mieux — sans ce garde-fou, un produit à
0,00 € gagnerait toujours.

**Le tarif dégressif ne vit qu'à un endroit** : `PriceTier.UnitPriceFor`, appelé par
`Product.UnitPriceFor` ET par `PaperOption.TotalPrice`. Deux règles de tarif finiraient par
diverger, et l'écart se verrait en caisse.

**L'opérateur peut imposer le papier** (`Choose(..., forcedPaperCode:)`), depuis la liste de
la barre de `PhotoGridView` : lui seul sait quel rouleau est chargé. Le choix imposé
l'emporte sur tout le calcul ; s'il ne peut pas porter la case, aucun plan n'est rendu et
l'écran le dit.

**Les places se comptent en PIXELS, jamais en millimètres.** `CustomSheetLayout` s'appuie sur
`IdSheetLayout.MaxCopies`, celui-là même qui posera la grille. Compter en millimètres
donnerait parfois une case de plus que la planche n'accepte, et le rendu échouerait après que
l'opérateur a annoncé son prix.

**Trois portes d'entrée**, et une seule mécanique derrière :

1. la tuile « Personnalisé » de `PrintFormatView` (parcours complet) ;
2. l'entrée « Personnalisé… » du menu de format, dans la grille ET dans « Modifier »
   (`ProductMenu.Ouvrir(..., personnalise:)`) — elle bascule les photos DÉJÀ ouvertes, en
   gardant recadrages et corrections ;
3. le bouton « Personnalisé… » d'une commande de borne, qui ouvre ses photos directement
   dans la taille demandée au lieu du format commandé.

**Les commandes de bornes sont listées à DEUX endroits** : l'accueil (`HomeView`) et
l'écran dédié (`KioskOrdersView`). Toute action de ligne ajoutée à l'un doit l'être à
l'autre — l'accueil est celui qu'on regarde en servant un client, et une action qui n'y
figure pas passe pour absente.

**Le COMPORTEMENT, lui, ne vit qu'à un endroit : `OuvertureBorne`.** Les deux écrans
avaient chacun leur copie d'`Archiver` + `MarkInProgress` + `Navigator.Go`, mot pour mot.
Ce qui se perdait à l'ouverture se perdait donc deux fois et se corrigeait une seule. Les
BOUTONS se doublent ; ce qu'ils font, non.

Les portes 2 et 3 existent parce qu'une borne ne propose que des formats standard : le client
commande du 10×15 et demande autre chose au comptoir. Sans elles, il fallait repartir de
l'accueil et retrouver le dossier, en perdant tout le travail déjà fait.

**Une bascule change TOUTES les photos**, cochées ou non : la planche est une seule ligne de
commande, elle ne peut pas mélanger deux tailles.

**Côté écran**, `PhotoGridView` fabrique un `Product` fantôme (`_produitPerso`, code
« perso ») pour que cadres, vignettes et écran « Modifier » travaillent au bon rapport
largeur/hauteur sans rien savoir du format. Il est remplacé par le papier retenu au moment de
bâtir la commande : **ce code fantôme ne descend jamais jusqu'à la commande.**

---

## Le cadrage d'une commande de borne : quatre affectations, dans cet ordre

Ouvrir une commande de borne dans « Modifier » doit reposer sur chaque vignette ce que le
CLIENT a validé à la borne. Deux moitiés, et il faut les deux.

**1. La donnée doit voyager.** `Archiver` recopie les FICHIERS ; `PhotoGridView` rescanne
le dossier et ne voit donc que des images. `StagedOrder.Cadrages` (nom de fichier →
`CadrageBorne`) porte le reste — recadrage, quarts de tour, redressement, quantité,
produit de la ligne. Sans lui, le parcours « Reprendre » gardait le cadrage et le parcours
« Modifier » le jetait : **le même bouton ne tirait pas la même chose selon l'écran.**
`CropOf` et `QuarterTurns` sont partagés par les deux parcours, pour qu'ils ne divergent
jamais.

**2. L'ordre de pose est imposé.** Trois mutateurs de `PhotoItem` remettent le cadrage à
zéro :

| Mutateur | Effet de bord |
|---|---|
| `Product` (code différent) | `OublierCadre()` → `_cadre = null`, `Crop = Full` |
| `RotationQuarterTurns` | idem |
| `FitOverride` (valeur différente) | `_cadre = null`, `CadrageImpose = false` |

D'où **produit → quarts de tour → redressement → recadrage**, et à un seul endroit :
`PhotoGridView.AppliquerLeCadrageDeLaBorne`. Le produit est posé **à la création**, et pas
seulement parce qu'il vient en premier : sans lui, le `photo.Product ??= DefaultProduct`
d'`OnModify` le poserait plus tard et emporterait le recadrage avec lui.

**`PoserLeCadrageDOrigine` et `CadrageImpose` existent pour les « bord blanc ».** Le
getter `Cadre` ignore le recadrage enregistré en mode « photo entière » — juste dans son
cas d'origine (un cadrage hérité du mode « remplir » ferait déborder la photo), mais il
jetait aussi le cadrage validé par le client sur les **dix produits bord blanc** du
catalogue, tous en `DefaultFit = Fit`. Le drapeau dit « ce recadrage est une décision, pas
un héritage », et tombe dès que l'opérateur reprend la main sur le format.

Vérifié par `tests/Studio.Tests/KioskCropCarryTests.cs`, sur les valeurs réelles de la
boutique (image 1536 × 2048, crop `X=0 Y=44 W=1536 H=1958`). **L'ordre des affectations,
lui, n'est pas couvert** : `Studio.App` n'est pas référencé par les essais.

---

## « Mettre en attente » : toute commande en préparation, pas seulement les bornes

`DataRoot\attente\<guid>.json`, un fichier par commande — voir `TravailEnAttente` et
`AttenteStore`.

C'est le geste du comptoir : un client hésite ou s'absente, un autre attend derrière. La
première version ne valait que pour les commandes de bornes, et c'était le contresens :
**c'est en préparant une commande AU COMPTOIR qu'on a besoin de faire autre chose**, et
l'origine des photos n'y est pour rien. L'identité est donc un `Guid` propre, et non l'oid
d'une borne — une clé USB ou un envoi de téléphone n'en ont pas.

**Deux boutons, parce qu'on s'interrompt à deux endroits** : la grille (`PhotoGridView`) et
surtout l'écran « Modifier » (`EditSelectionView`), là où l'on passe le plus de temps sur
une commande. Mais **c'est toujours la GRILLE qui enregistre** : « Modifier » ne tient que
les photos COCHÉES, et mettre de côté la moitié d'une commande serait pire que de ne rien
mettre de côté.

Sept règles à ne pas défaire :

1. **Les photos sont désignées par leur NOM DE FICHIER**, jamais par leur rang. Un fichier
   illisible est écarté au chargement de la grille (`Ecarter`) : les rangs se décaleraient
   d'une ouverture à l'autre, et on reprendrait le cadrage du voisin.
2. **L'écran garde son `_attenteId` pour toute sa vie.** Remettre de côté met à jour la même
   entrée ; sans cela, chaque aller-retour laisserait un doublon sur l'accueil et on ne
   saurait plus laquelle reprendre.
3. **L'enregistrement est explicite**, jamais automatique. Mettre en attente en quittant
   l'écran ferait s'accumuler des commandes qu'on n'a fait qu'ouvrir, et la liste de
   l'accueil ne voudrait plus rien dire.
4. **Le bandeau de l'accueil disparaît quand il n'y a rien.** Un titre suivi du vide fait
   croire à un écran cassé, et mange la place de la liste des bornes.
5. **Reprendre n'efface pas l'entrée** : l'opérateur peut la remettre de côté aussitôt, ou
   fermer l'écran sans rien décider. Elle part à l'impression (`OnPrint`) ou quand il
   l'abandonne.
6. **Pour une commande de borne, c'est `KioskOrderJournal` qui efface** —
   `MarkPrinted`, `Dismiss`, `Purge`, via `AttenteStore.EffacerPourBorne` — et non les
   écrans. Lui seul sait qu'une commande est close ou périmée ; le laisser aux appelants
   reviendrait à répéter la règle partout et à l'oublier au prochain. `EffacerPourBorne`
   ne touche QUE ce qui porte cet oid : une commande du comptoir mise de côté au même
   moment n'a rien à voir avec elle.
7. **L'attente l'emporte sur le cadrage du client**, et « ✕ attente » est la SEULE porte de
   retour vers celui-ci. Sans elle, une mise de côté serait définitive.

La taille personnalisée fait partie de l'entrée : rouvrir au format du catalogue un travail
fait en 5,5 × 8 remettrait tous les cadres au centre, au mauvais rapport.

**`AppServices.CommandesEnAttente` n'est PAS `AppServices.Attente`** : la seconde est la
file des tirages que l'IMPRIMANTE fait attendre (`PendingPrintQueue`). Ici, c'est
l'opérateur qui met de côté, et rien n'est encore commandé.

---

## Détourage du fond blanc : deux méthodes, et un réglage par poste

`BiRefNetMatting.Actif` était un `bool` statique à faux **qu'aucune ligne du dépôt
n'assignait** : le réseau ne s'exécutait jamais et tout ce code était mort. Il est posé
depuis `config\detourage.json` (écran Paramètres), via `AppServices.AppliquerLeDetourage`.

| | Poids | Sur la Quadro P2000 de l'atelier |
|---|---|---|
| méthode par couleur (`BackgroundRemoval`) | — | ~1,2 s, aucune exigence, marche toujours |
| `birefnet-lite-fp16` | 109 Mio | 4,3 s aperçu · 9,5 s pleine résolution · tient photo après photo |
| `birefnet-portrait-fp16` | 467 Mo | 1ʳᵉ photo passe, 2ᵉ échoue (`DmlFusedNode`, mémoire) |

**Le défaut reste la méthode par couleur** — décision de l'exploitant, 03/08/2026 : dix
secondes devant un client, c'est trop long. Le réglage ouvre la porte, il ne la franchit pas.

**Trois BOUTONS RADIO, et rien de grisé.** La première version posait deux cases à cocher
dont la seconde — le modèle puissant, celui qu'on vient justement chercher — restait
désactivée tant que la première n'était pas cochée. Elle n'était donc « pas cochable », et
rien à l'écran ne disait pourquoi. Un choix exclusif entre trois méthodes est ce qu'est
réellement ce réglage ; il n'y a plus d'état intermédiaire à expliquer.

Trois points à ne pas défaire :

1. **`SaveDetourage` appelle `BiRefNetMatting.Reinitialiser()`.** La session ONNX est
   gardée pour la vie du processus (recharger un demi-gigaoctet par photo bloquerait le
   comptoir) : sans cette remise à zéro, changer de modèle dans Paramètres n'aurait d'effet
   qu'au redémarrage suivant, et le réglage passerait pour inopérant.
2. **`ModelePrefere` passe en tête de la recherche, il ne l'épuise pas.** Modèle absent, on
   retombe sur l'autre — mais `Session()` l'écrit au journal. Un modèle silencieusement
   remplacé ferait chercher le défaut ailleurs. `ModeleRetenu`, lui, est SANS effet de
   bord : l'écran des réglages l'interroge à chaque frappe.
3. **La mémoire vidéo se lit dans le REGISTRE, pas dans WMI.**
   `Win32_VideoController.AdapterRAM` est un `uint32` en octets : il plafonne à 4 Go et
   rend donc « 4 Go » sur toutes les cartes de 6, 8 ou 24 Go — l'avertissement se serait
   déclenché à tort exactement dans le cas qu'on cherche à autoriser.
   `HardwareInformation.qwMemorySize` donne la vraie valeur (la P2000 de ce poste : **5
   Go**, et non les 4 qu'annoncent les commentaires plus anciens).

Les modèles vivent **hors du dépôt**, dans `DataRoot\models\` : le dépôt est public, un
demi-gigaoctet de poids n'y a rien à faire. L'écran Paramètres dit lesquels sont installés
et où poser celui qui manque.

---

## Contours de découpe

Deux chemins, une seule règle : **trait noir de 0,2 mm, tracé À CHEVAL sur le bord**, pour que
le coup de ciseaux l'emporte. Un trait posé à l'intérieur laisserait un liseré noir sur la photo
coupée ; posé à l'extérieur, il laisserait du blanc.

| Où | Réglage | Tracé |
|---|---|---|
| Agrandissement | `LargeFormatPrintSettings.CutBorder` | `LargeFormatPrinter`, après `DrawImage`, sur la page — le bitmap n'est pas touché |
| Tirage « photo entière » | `PhotoItem` → `DraftItem` → `OrderItem` → `RenderRequest` → `ImagePipeline` | `ImagePipeline.DrawCutBorder`, sur les pixels du tirage |
| Planche identité | `SheetSpec.CutBorder` (existait déjà) | `ImagePipeline.DrawCutBorders` |

Deux points à ne pas défaire :

- **Sans objet en « remplir le format »** : la photo occupe tout le tirage, il n'y a rien à
  recouper. `ImagePipeline` ne trace alors rien et la case est grisée dans `EditSelectionView`.
- **Le trait est posé APRÈS les corrections et AVANT la conversion ICC.** Après, pour qu'une
  correction de luminosité ne délave pas le repère ; avant, pour que tout ce qui part sur le
  papier suive le même chemin couleur.
- L'épaisseur suit le **dpi du produit**, passé explicitement à `Render`/`RenderInto` : la
  densité du fichier n'est posée qu'à l'écriture, la lire depuis l'image donnerait celle de la
  source.

L'image convertie porte des valeurs **du périphérique** : la gestion des couleurs doit être
DÉSACTIVÉE dans le pilote, sinon elle est convertie deux fois. C'est ce que dit l'avertissement
de `LargeFormatPrintSettings.Validate`.

---

## `LargeFormatPrintView` ne décode jamais l'image entière avant l'impression

- **constructeur** : en-tête seul (`BitmapFrame` en `DelayCreation`), flux refermé aussitôt ;
- **aperçu** : un seul `BitmapImage` réduit (`DecodePixelWidth`), gelé, réutilisé par tous les
  redessins via un `ImageBrush` gelé ;
- **imprimante et format papier** : interrogés en tâche de fond ;
- **image pleine résolution** : lue dans `OnPrint`, sur un fil de fond, une seule fois.

Ne jamais remettre un décodage dans `DrawPreview` : il est appelé à chaque clic, chaque frappe et
chaque redimensionnement. C'est ce qui gelait la fenêtre et faisait que les cases « ne
fonctionnaient qu'une fois » — les clics suivants tombaient pendant le décodage.

Autres pièges de cet écran :

- **Changer d'orientation oblige à redemander la taille de feuille au pilote**
  (`RefreshPageSizeAsync`). Recalculer le placement ne suffit pas : `_pageWidthMm` /
  `_pageHeightMm` ne permutent pas tout seuls.
- **Le `Canvas` d'aperçu doit rester `ClipToBounds="True"`** (le `Border` aussi). Un `Canvas` ne
  rogne jamais ses enfants : sans cela, un tirage qui déborde de la feuille se dessine par-dessus
  toute la fenêtre, y compris l'avertissement qu'il faut justement lire.
- **Résolution absente = 300 ppp.** GDI+ ne rend jamais 0, il invente 96, et l'ancien test
  `> 1` le laissait passer : un fichier sans densité partait trois fois trop grand. Les rendus de
  l'atelier portent tous leur densité (`ImagePipeline` la pose) ; ce repli ne vaut que pour les
  fichiers venus du dehors.

---

## Le code QR WiFi se lit dans `config/wifi.json`, pas dans Windows

`WifiQr.Current()` sait lire le profil sans fil de Windows — par l'**export XML**
(`netsh wlan export profile`) et non par l'affichage texte, qui est traduit et rendrait
« Key Content » sur un Windows anglais.

**Sur le poste de l'atelier, cette lecture ne rendra jamais rien** : la Precision 3620 n'a
aucune carte sans fil (`netsh wlan show interfaces` → « Il n'existe aucune interface sans fil
sur le système »). C'est `config/wifi.json`, rempli à la main, qui fait vivre le code — et il
l'emporte toujours sur la lecture automatique. Celle-ci ne sert que sur un portable.

Sans réseau connu, la colonne WiFi de `PhoneUploadView` **disparaît** et le code d'envoi
reprend toute la place. C'est voulu : un poste en Ethernet est un cas normal, et
l'avertissement ne servirait à personne.

---

## DNP DS620 : son ÉTAT vient du spouleur, pas du SDK

Le SDK DNP (`CPPCtrl32.dll`) ne peut pas ouvrir la DS620 tant que DiLand tourne — il tient
le port USB en exclusif, et le SDK se bloque au lieu de le dire (voir `DiLandPresence`).
Or **DiLand tourne pratiquement en permanence** en boutique : c'est lui qui reçoit les
commandes des bornes.

Faute de réponse du SDK, les écrans affichaient **« En veille » en continu** — machine
allumée, prête, et même pendant qu'elle tirait. Signalé par l'exploitant le 04/08/2026.
« Muet au SDK » et « endormie » ne sont pas la même chose, et c'est cette confusion qui
faisait tout.

**`DnpSpouleur` lit l'état dans le spouleur Windows** (WMI `Win32_Printer` +
`Win32_PrintJob`) : c'est par lui que Studio imprime sur cette machine, il répond toujours,
et il voit ce qu'un SDK bloqué ne peut pas voir.

| | Ce qu'il donne |
|---|---|
| spouleur | état de la file, pannes du pilote, **photos restant à sortir** |
| SDK (DiLand fermé) | rouleau restant, numéro de série, micrologiciel |

Cinq points à ne pas défaire :

1. **Une page = une photo.** `BitmapPrinter` envoie un travail d'UNE page par tirage, et
   `PrintOrchestrator` le répète par exemplaire. `Σ(TotalPages − PagesPrinted)` est donc
   exactement le nombre de photos qu'il reste à sortir — c'est ce que l'exploitant regarde
   pour savoir s'il a le temps de servir quelqu'un d'autre. Compter les TRAVAUX ferait
   stagner l'affichage sur celui qui est à moitié sorti.
2. **`Win32_Printer.PrinterState` n'est PAS lu.** La DP-DS620 le laisse à 0, ce que la
   documentation traduit par « Paused », alors qu'elle est prête et qu'elle imprime. S'y
   fier remettrait le défaut qu'on vient de corriger. Relevé le 04/08/2026 :
   `PrinterStatus = 3`, `WorkOffline = False`, `DetectedErrorState = 0`, `PrinterState = 0`.
3. **`DetectedErrorState` 0 et 2 ne sont pas des pannes** (« inconnu » et « aucune
   erreur »). La machine de la boutique rend 0 en marche normale : la traiter comme une
   erreur repeindrait la tuile en rouge en permanence.
4. **L'ordre de décision** : hors ligne → panne → file en pause → impression → prête. Une
   panne reste une panne même avec des tirages qui patientent derrière, et une file en
   pause n'est pas « en cours » alors que rien n'en sortira.
5. **La pause d'une file VIDE n'est pas détectée**, et c'est assumé : elle se lirait dans
   `PrinterState`, justement inutilisable ici. La pause se voit dès qu'un travail attend
   (bit `0x0001` de `Win32_PrintJob.StatusMask`, non traduit — `JobStatus` est du texte
   localisé et ne se compare pas).

Les couleurs sont **les mêmes que celles du minilab**, DNP comprise : vert prête, bleu en
train de tirer, orangé un geste à faire, rouge en panne, gris hors ligne, ardoise en
veille. `MachineStatusView` porte désormais une pastille — tout y était du texte, et il
fallait lire chaque ligne pour savoir laquelle des machines réclamait quelque chose.

`MachineStatusView` se relit **toutes les dix secondes** : le nombre de photos restantes y
serait resté figé pendant toute une commande, c'est-à-dire au moment précis où on le
regarde. La relecture automatique est *discrète* — ni sablier ni liste vidée, sinon l'écran
clignoterait et volerait le curseur.

`Studio.PrintProbe dnp` montre ce que le spouleur dit, sans rien imprimer — le contrôle qui
manquait.

---

## DNP DS620 : elle ne connaît QUE ses onze formes de papier

`PrinterSettings.PaperSizes` de la DP-DS620 ne publie **aucun format standard** : onze
formes privées, RawKind 119 à 129, toutes rendues `PaperKind.Custom`.

| Forme | centièmes de pouce | mm |
|---|---|---|
| (6x4) | 615 × 413 | **156,2 × 104,9** |
| PR (4x6) | 413 × 615 | 104,9 × 156,2 |
| (5x7) | 516 × 713 | 131,1 × 181,1 |
| (6x8) | 615 × 812 | 156,2 × 206,2 |
| (6x9), (6x6), (6x4.5), (5x5), (5x3.5), PR (3.5x5), PR (4.5x6) | | |

**Un `PaperSize` fabriqué par nous porte RawKind 0, soit `DMPAPER_USER`. La machine accepte
le travail et le JETTE** — pas d'erreur, pas de page, rien dans le journal du spouleur.
C'est ce qui est arrivé aux planches d'identité les 01 et 02/08/2026 : le journal de
l'application montre « page obtenue 152×102 mm (**Format produit**) », et aucune planche
n'est sortie. Le produit était déclaré à 152 × 102 quand la forme `(6x4)` en fait 156,2 ×
104,9 ; l'ancienne recherche n'admettait que 1,5 mm d'écart et ne trouvait donc rien.

Trois règles à ne pas défaire :

1. **Les cotes d'un produit DNP doivent tomber sur une forme du pilote**, au demi-millimètre
   près. `BitmapPrinter.ChoisirFormat` retient la plus proche, **jamais plus petite** que le
   tirage (elle rognerait) et jamais plus de 2,5 mm plus grande (l'image est dessinée aux
   dimensions de la page retenue, donc elle s'étirerait — sur une planche d'identité, un
   millimètre suffit à faire refuser la photo au guichet).
2. **Une imprimante qui ne déclare que des formes privées et à qui aucune ne convient est
   refusée**, en nommant ce qu'elle accepte. Les pilotes qui savent composer un format libre
   (Print to PDF, XPS) publient, eux, des formats standard : le repli leur reste ouvert.
3. **Le contrôle a lieu dans `PrintOrchestrator`, AVANT le rendu et avant l'état
   « Spooled »** (`BitmapPrinter.EnsurePageSizeAvailable`). Le même contrôle existe dans
   `Print`, mais il y survient trop tard : l'enveloppe serait proposée à la réimpression au
   démarrage suivant alors que rien n'est parti.

`Studio.PrintProbe papier <imprimante> <Lmm> <Hmm>` répond à la question sans gâcher une
feuille — à passer avant d'enregistrer un produit au catalogue.

---

## Planche identité : la case suit le DOCUMENT, le papier suit le produit

| | Ce qui la fixe |
|---|---|
| taille du tirage | `Product.WidthMm/HeightMm` — une forme du pilote (voir ci-dessus) |
| taille d'une case | `OrderItem.SheetCellWidthMm/HeightMm`, soit le **document visé** |
| repli | `Product.Sheet.CellWidthMm/HeightMm` quand l'article n'en porte pas |

`IdDocumentPickerView` propose 274 documents (France 35 × 45, Espagne 26 × 32, USA 51 × 51…)
et le gabarit de `IdPhotoView` suit la norme choisie. **La planche imprimée, elle, sortait
toujours en 35 × 45** : `PrintOrchestrator` lisait la cellule du produit, qui n'en connaît
qu'une. Le champ sur l'article existe pour ça, et il est `double?` — les commandes déjà
enregistrées n'en portent pas et retombent sur le produit.

**La capacité se compte en PIXELS, par `IdSheetLayout.MaxCopies`** — celui-là même qui
posera la grille au rendu. Compter en millimètres donnerait parfois une case de plus que la
planche n'accepte, et l'impression échouerait après que l'opérateur a annoncé son prix.

`IdPhotoView` **écarte de la liste les papiers qui ne portent pas une seule case** du
document, et propose la planche PLEINE par défaut : le « planche de 8 » inscrit au produit
vaut pour le format français, et une planche est facturée au papier — laisser des places
vides revient à les vendre.

---

## Le noir sur noir : WPF ne connaît pas notre palette

L'application est sombre (`PageBrush #12181E`, `CardBrush #1E2731`), mais **WPF donne le
NOIR à tout `TextBlock` qui n'hérite pas d'autre chose**. Douze écritures étaient donc
purement invisibles (quantités, totaux, moitié de la fiche produit).

**RÈGLE : tout `TextBlock` posé directement dans un `Grid`, un `Border` ou une `StackPanel`
doit porter une couleur** — `Texte`, `Valeur`, `Hint`, `PageTitle`, ou la sienne.

Deux choses à savoir avant de « simplifier » :

1. **Un `TextBlock` placé dans un `Button` ou un `Control` hérite de SA couleur**
   (`TextElement.Foreground` est héritée, et `Control.Foreground` en est un alias). Les
   styles `BigButton`, `FlatButton`, `LigneListe`, `Secondaire` en fixent une : à
   l'intérieur, il n'y a rien à faire. C'est ce qui rend le relevé automatique trompeur —
   sur 22 candidats trouvés par expression régulière, dix étaient de fausses alertes.

2. **Aucun style IMPLICITE `TargetType="TextBlock"` n'est posé, et c'est délibéré.** Il
   repeindrait aussi le contenu des `ComboBoxItem` et `ListBoxItem`, qui gardent le fond
   CLAIR du système : on troquerait du noir sur noir contre du blanc sur blanc. Le jour où
   on voudra vraiment un style implicite, il faudra habiller les listes et les menus
   déroulants d'abord.

---

## Le format POLA : un cadre Polaroid, pas une marge blanche

Cotes du film 600 / i-Type, publiées par Polaroid : tirage **88,47 × 107,52 mm**, fenêtre
image **78,94 × 76,80 mm**. Elles sont dans `PolaroidFrame`.

Deux choses font la forme, et il ne faut « arranger » ni l'une ni l'autre :

- **la fenêtre est presque CARRÉE** (1,028) — d'où un recadrage serré ;
- **la bande du bas fait 25,95 mm**, contre 4,77 aux trois autres bords : près du quart de
  la hauteur.

**Le cadre garde ses proportions et se centre** dans le tirage. Un 10×15 a le rapport 0,671
quand le Polaroid a 0,823 : le cadre occupe donc toute la largeur et laisse du blanc en haut
et en bas. Le contour de découpe marque le vrai bord — c'est là qu'on coupe. L'étirer pour
remplir la feuille donnerait un rectangle qui n'est plus un Polaroid, et c'est justement la
forme qu'on cherchait.

**Le rendu compose, il ne retouche pas sur place.** `RenderPolaroid` fabrique une image
blanche aux dimensions du tirage et y pose la photo (`Composite`), comme
`RenderIdSheetToFile`. Un `Extent` décentré ferait la même chose en trois lignes de moins,
au prix d'un signe qu'on relit trois fois.

**La teinte vient avec le cadre** : `InverseLevel(7 %, 95 %)` pour le voile — c'est ce qui
se voit le plus —, saturation −12 %, rouge +4 %, bleu −3 %. Pas de vignettage ni de grain :
plusieurs secondes par tirage pour un effet que le cadre donne déjà. Elle n'est pas
débrayable, à dessein : qui veut des couleurs franches prend un tirage ordinaire.

Côté écran, **`CropEditorView` et la grille montrent la FENÊTRE**, pas la feuille — sans
quoi l'opérateur cadre sur des bords qui seront coupés. La bascule Remplir/Entier est grisée
sur un Polaroid : en sortir donnerait un tirage sans cadre que rien ne signalerait.

---

## Fiche produit : ce qu'elle ne montre pas, elle doit le CONSERVER

`Product` a plus de champs que la fiche n'en affiche. Deux règles, apprises en les cassant :

1. **`Product.Copy()` est la seule copie**, et elle vit dans le domaine — pas dans l'écran —
   pour qu'un essai puisse la vérifier par réflexion. Quand elle était dans `CatalogView`,
   elle oubliait `Output`, `MinilabMachineId`, `MinilabPrintSizeName`, `PriceTiers` et
   quatre réglages de planche : **modifier un tirage du minilab le transformait en produit
   imprimante** et effaçait ses paliers de tarif. `CatalogEditTests` échoue désormais dès
   qu'une propriété est ajoutée à `Product` sans être recopiée.

2. **`ProductEditView.OnSave` ne recrée jamais un `SheetSpec` à neuf** : il modifie celui du
   produit. La fiche ne montre que trois de ses sept réglages ; les recréer effaçait
   l'horodatage exigé par l'administration sur les photos d'identité.

**Un produit ne se supprime que s'il n'est cité par AUCUNE commande récente**
(`ProductCatalog.CountReferences`, fenêtre de 30 jours — celle des écrans qui montrent les
commandes). Tout le circuit d'impression appelle `Require(code)`, qui lève si le code a
disparu : supprimer un produit encore cité rendrait ces commandes impossibles à réimprimer,
et c'est justement l'écran des commandes du jour qui sert à rattraper un tirage raté. Le
Catalogue propose alors de le DÉSACTIVER.

---

## Agrandissement à taille libre : un vrai produit, créé à la volée

Le « Personnalisé » des agrandissements n'a **rien à voir** avec celui de l'impression
rapide : celui-là compose des planches sur du papier minilab (`CustomSheetLayout`), celui-ci
sort un tirage unique en fichier pour l'Epson.

**Le prix est celui du format du catalogue dans lequel la taille tient.** Décision de
l'exploitant, 02/08/2026 : « si ça tient dans un 30×40, le prix d'un 30×40 ; si c'est dans
un 40×50, le prix d'un 40×50. » Départages dans `EnlargementSizes.PaperFor` : le moins cher
d'abord — c'est le prix qu'on annonce —, puis le plus petit. **Un produit à 0,00 € est
écarté** : le 70×100 n'est pas tarifé et raflerait tout au moins-cher.

Le piège du catalogue réel : un A3 (297 × 420) **ne tient pas** dans un 30×40 (300 × 400).
Il faut le 30×45, et sept euros séparent les deux.

**La taille demandée devient un vrai produit du catalogue** (`agr-297x420`), ajouté à la
validation. C'est ce qui fait fonctionner la grille, « Modifier », le rendu, la boîte grand
format — et surtout `Require` des semaines plus tard, à la réimpression. Le code est
déterministe et indifférent au sens, sinon le catalogue se remplirait d'un doublon par
commande. Un produit déjà présent n'est jamais retarifé : son prix a pu être ajusté à la
main, et une commande passée ne doit pas changer de montant parce qu'on refait le même
format.

---

## Le catalogue de la boutique vit HORS du dépôt

L'application lit `D:\PhotoStudioData\catalog\products.json`. Ce fichier n'est pas dans git :
**changer un prix, un format ou capturer des réglages pilote ne se voit pas dans
`git status`**, et le seul filet est `products.json.bak`, qui ne garde que la version d'avant.

`catalog/boutique/` en tient une copie versionnée. Elle n'est **pas lue par l'application** —
c'est une sauvegarde, rien d'autre. `tools\Sauver-Catalogue.cmd` la rafraîchit ; à lancer
après chaque changement du catalogue, puis à committer. Le vrai risque de ce dossier est
qu'on l'oublie : une copie vieille de trois mois donne une fausse impression de sécurité.

**Le dépôt est PUBLIC.** Trois choses n'y entrent donc jamais :

| | Pourquoi |
|---|---|
| `config\wifi.json` | il porte le **mot de passe** du réseau de la boutique |
| `catalog\icc\*.icc` | fichiers du fabricant, et réimportables en deux clics depuis Catalogue |
| `orders\`, `logs\`, `archive\` | noms de clients, photos, et ça grossit tous les jours |

Les prix, eux, ne sont pas un secret : ils sont affichés au comptoir, et `products.diland.json`
comme `diland-prices.json` les portent déjà dans le dépôt.

---

## Environnement : la clé de registre du SDK Fuji

Le SDK DE100 (`PModuleIF.dll`, chargé par le relais 32 bits) crée
`HKLM\SOFTWARE\WOW6432Node\Fujifilm\Frontier\CurrentVersion\System\Debug` au démarrage. Cette
clé n'accorde que la LECTURE au groupe Utilisateurs : hors élévation, l'appel échoue avec
`RegCreateKeyEx` erreur 5 (accès refusé). Ce n'est pas un défaut de l'application.

---

## DE100 : une ENVELOPPE = une commande minilab

`PIF_StartOrder` une fois, `PIF_Print` par photo, `PIF_EndOrder` une fois. La signature du
SDK ne laisse pas le choix :

```c
PIF_Print(orderHandle, &imageData, params, n);          // le handle est un PARAMÈTRE
PIF_GetPrintInfo(orderHandle, index, &printInfo);       // les tirages se relisent par INDICE
```

Une commande porte N images, et c'est ainsi que procède le pilote de DiLand sur les 9 336
tirages de son journal.

**Ce que coûtait l'inverse.** Studio ouvrait une commande PAR PHOTO. Commande 04-007 du
04/08/2026 : quatre `StartOrder`/`EndOrder` en 1,2 s, quatre handles revenus `Ok` —
**deux tirages sur quatre ne sont jamais sortis**, sans erreur, sans trace. Rien ne
garantit ce va-et-vient, et c'est le seul candidat compatible avec les faits.

Quatre points à ne pas défaire :

1. **La commande part ENTIÈRE ou pas du tout.** Un `PIF_Print` refusé à la troisième photo
   annule les deux premières (`PIF_CancelOrder`). Une demi-commande ouverte côté minilab
   est exactement le genre d'ordre fantôme qui bloque sa file.
2. **L'arrêt s'examine pendant la PRÉPARATION des images**, plus entre deux envois — il n'y
   en a plus qu'un. Demandé pendant l'envoi, il reste rattrapable : un seul handle à
   rappeler au lieu d'un par photo, mais le bouton d'arrêt garde le même pouvoir.
3. **`De100JobTracker` porte plusieurs `JobId` sous un handle.** Le minilab notifie par
   COMMANDE : `Report` rend donc une issue PAR PHOTO. En rendre une seule laisserait cinq
   sur six sans verdict, et le compte des tirages restants ne descendrait jamais.
   `PendingCount` compte les TIRAGES, pas les commandes.
4. **`De100SubmitRequest` n'a QU'UN constructeur.** Un second — le confort « un tirage
   seul » — laisse `System.Text.Json` sans règle pour choisir et fait échouer la
   désérialisation : le relais refuserait toutes les demandes de tirage. Pour un tirage
   seul, on passe une liste d'un élément.

**Le verdict du minilab s'écrit au JOURNAL** (`AppServices`, sur `JobFinished`). Il ne
l'était nulle part : le fichier de 04-007 s'arrête à l'envoi, et c'est ce qui a rendu
l'enquête impossible. `De100BridgePrinter.EnsureConnected` vide aussi `_subscribed` : le
relais redémarre (deux fois le 04/08/2026), et sans cela on ne se réabonnait jamais — plus
aucun tirage ne recevait son verdict, en silence, pour toute la vie de l'application.

---

## Les PDF : une page = une photo, éclatée AVANT la planche

`PdfPages.Developper` remplace chaque PDF par ses pages, **à sa place dans la liste**, au
moment du scan de dossier. Rien en aval — rendu, minilab, DNP, commande — ne sait qu'un PDF
existe.

**PDFium, et non Ghostscript.** Magick.NET ne lit pas un PDF seul : il délègue à
Ghostscript, qui n'est installé sur aucun poste de la boutique. `PDFtoImage` embarque
PDFium en natif — rien à installer, et pas d'AGPL dans un dépôt public.

| | |
|---|---|
| résolution | **200 ppp** — un A4 donne 1654 × 2339 px, de quoi tirer un 13×18 à 300 ppp sans interpoler. 300 quadruplerait la mémoire pour un gain que le papier ne rend pas |
| plafond | **60 pages** — une notice de 400 pages posée par erreur remplirait la planche et le cache avant qu'on ait vu ce qui se passe |
| où | `DataRoot\cache\pdf\<empreinte>\` — **jamais à côté du PDF** : le dossier ouvert est la clé du client, on n'y écrit rien |

**Le témoin `pages.txt` n'est écrit qu'une fois TOUTES les pages posées.** Une extraction
interrompue — coupure, clé retirée — se refait donc au lieu de rendre une commande
incomplète.

**`.pdf` est dans `PhotoScanner.Extensions`**, sans quoi un dossier de scans serait annoncé
vide et disparaîtrait de l'écran de parcours. Mais un PDF **n'illustre jamais un dossier**
(`FirstPhoto` l'écarte : la vignette est décodée telle quelle), et les deux écrans qui ne
savent pas l'éclater le filtrent par `PhotoScanner.IsPdf` — l'identité (on ne fait pas une
photo d'identité depuis un document) et la borne (libre-service, personne pour aider).

---

## Les photos se présentent de la plus RÉCENTE à la plus ancienne

`PhotoScanner.TrierParDateDecroissante`, posé au chargement des trois écrans qui scannent.
Ce que le client veut tirer est ce qu'il vient de prendre ; l'ordre alphabétique le
renvoyait en bas de mille vignettes.

Trois points :

1. **`Scan` garde son tri ALPHABÉTIQUE.** Il est déterministe, testé, et c'est lui qui
   décide de ce qui rentre sous le plafond de 1 200. Le classement par date est posé
   après, à l'affichage seulement.
2. **La date retenue est la plus ANCIENNE des deux** que Windows tient. Copier une carte
   mémoire remet la création à l'instant de la copie et met toutes les photos du client à
   la même seconde ; la modification, elle, survit à la copie.
3. **À date égale, le nom départage.** Une rafale sort à la même seconde, et son ordre ne
   doit pas changer d'une ouverture à l'autre.

Le bouton « trier » de la grille bascule désormais vers le NOM et revient — il partait du
nom pour aller vers la date.

---

## `ToucheFenetre` : un abonnement clavier qui ne se double pas

Écouter le clavier de la FENÊTRE est obligatoire pour T (redressement) : un événement
clavier remonte depuis ce qui a le focus, et le focus n'est pas sur la photo tant qu'on n'a
pas cliqué dedans — le geste que l'opérateur ne fait pas.

Le branchement se faisait ainsi, dans `CropSurface` et dans `IdPhotoView` :

```csharp
Loaded   += (_, _) => Window.GetWindow(this).PreviewKeyDown += OnPreviewKeyDown;
Unloaded += (_, _) => Window.GetWindow(this).PreviewKeyDown -= OnPreviewKeyDown;
```

**WPF déclenche `Loaded` plusieurs fois** sur un même élément — reparentage, retemplatage —
sans `Unloaded` entre les deux. Le gestionnaire se retrouvait abonné DEUX FOIS, et comme T
est une bascule, un appui la jouait deux fois : le mode ne s'armait jamais et le bandeau
n'apparaissait pas. Signalé le 04/08/2026 (« je ne peux pas redresser avec la molette en
appuyant sur T »), visible au journal : `T=False (armé=False)` juste après l'appui.

`ToucheFenetre` rend l'abonnement idempotent ET **retient la fenêtre** : se désabonner via
`Window.GetWindow` au moment de l'`Unloaded` peut viser une autre fenêtre, ou aucune —
l'élément est déjà détaché de l'arbre visuel.

---

## Les boutons du panneau de recadrage portent sur les photos VISÉES

`OnToggleFit`, `OnRotateFrame`, `OnRotatePhoto`, `OnResetCrop` passent par
`EditSelectionView.SurLesVisees`, comme les corrections et le contour de découpe. Ils
écrivaient sur `_courante` et sur elle seule : **Ctrl+A puis « Remplir » ne changeait
qu'une photo sur trente.**

**Le mode est déduit de la photo COURANTE puis IMPOSÉ aux autres**, jamais basculé photo
par photo : sur une planche à moitié en « remplir », basculer chacune de son côté les
inverserait sans jamais les aligner, alors que le geste demandé est « mets-moi tout en
remplir ».

Chaque photo lit le défaut de SON produit (`FitOverride = voulu == sien.DefaultFit ? null :
voulu`) : une planche peut mélanger deux formats, et le mode ne s'exprime que par rapport
au défaut de son propre produit.

La ligne de journal « … sur N photo(s) » n'est pas une politesse : aucun essai ne clique,
c'est le seul moyen de vérifier après coup qu'un geste a porté sur la planche entière.

---

## Photo d'identité : impression en tâche de fond, et corrections

**`IdPhotoView.OnPrint` ne rend plus la main après le tirage, mais après la CRÉATION de la
commande** — comme la grille. Il attendait `PrintEnvelope` en entier puis affichait une
boîte de dialogue : c'était le seul parcours qui retenait l'opérateur devant sa machine
pendant que le client suivant attendait. Plus de boîte de succès : l'avancement, l'attente
d'imprimante et les échecs se lisent tous dans le bandeau du haut.

**Le module « Corriger »** ouvre `AdjustView` — le même écran que les tirages — sur la photo
courante. Trois règles :

1. **Noir et blanc et fond blanc n'y sont PAS passés** : ce sont des cases de l'écran
   identité. Les mettre aussi dans le module donnerait deux commandes pour un même
   réglage, dont l'une mentirait dès qu'on toucherait l'autre. `ReglagesRetenus()` les
   réunit au dernier moment, à un seul endroit — il y a trois sorties (planche, courriel,
   aperçu), et une quatrième oubliée tirerait autre chose que ce qu'on montre.
2. **Les corrections appartiennent à la photo courante** et repartent à neutre quand on
   change de photo dans la bande. Une planche d'identité ne porte qu'un visage.
3. **L'aperçu suit l'ordre du RENDU** — fond blanc, corrections, noir et blanc — celui
   d'`ImageAdjuster.Apply`. Le fond blanc raisonne sur les couleurs d'origine ; le noir et
   blanc vient en dernier, sans quoi les réglages de couleur n'auraient plus de prise.

**`IdPhotoFr.TargetCrownMarginMm` vaut 3,0 mm** (04/08/2026). Elle valait 4, puis 1,75 au
calage sur DiLand du 03/08 — trop serré sur les tirages réels. La TAILLE de la tête ne
bouge pas : seul le cadre remonte de 1,25 mm sur 45. Les bornes de conformité se calculent
depuis la cible et suivent toutes seules.
