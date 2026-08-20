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

**L'indication de taille au décodeur JPEG vaut la taille DEMANDÉE, pas son double.** Le
décodeur ne sait réduire que par 1/2, 1/4, 1/8, et choisit le premier facteur qui reste au
moins aussi grand que l'indication : lui demander 1024 pour une vignette de 512 lui faisait
décoder deux fois trop de pixels. Mesuré le 04/08/2026 sur vingt photos de 6 Mo
(commande 08-012) :

| | indication doublée | indication juste |
| --- | --- | --- |
| vignettes de la grille (512) | 3 557 ms | **2 378 ms** |
| aperçu « Modifier » / « Recadrer » (2048) | 10 037 ms | **4 425 ms** |

La vignette produite est à un kilo-octet près la même — sur un 7686 × 5124, l'indication
juste fait décoder du 961 × 641, encore près du double de la vignette finale. C'est
l'aperçu qui gagne le plus : à 4096, le décodeur ne pouvait plus réduire du tout et
dépliait les 39 Mpx.

**La définition de l'original voyage AVEC la vignette.** `Lire()` rend la vignette et les
cotes de l'original, mises en cache dans un fichier compagnon `.dim`. La grille les
demandait à `ImagePipeline.GetOrientedSize`, soit un SECOND parcours du fichier, payé même
quand la vignette était déjà en cache : rouvrir un dossier déjà vu touchait les
trente-trois originaux pour rien. Sur une carte SD, ce qui coûte est d'ouvrir le fichier,
pas de le décoder.

Un compagnon absent — les vignettes mises en cache avant lui — fait retomber sur la lecture
de l'original, une fois, puis le dépose. Rien à purger.

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

## Le spouleur est alimenté AU RYTHME DE LA MACHINE

`CadenceSpouleur`, appelée par `PrintOrchestrator.PrintPages` avant chaque page.

**Ce qu'on faisait avant** : remettre les pages au spouleur aussi vite qu'il les acceptait —
onze tirages en cinq secondes sur la commande 04-024 du 04/08/2026. Trois conséquences,
toutes vues en boutique :

1. **on ne pouvait plus reprendre au bon endroit.** Sur six cents photos, une panne d'encre
   à la troisième laissait quand même partir les cinq cent quatre-vingt-dix-sept autres :
   le point de reprise disait « 600 remises », la machine n'en avait sorti que deux ;
2. **la machine se bloquait** : une DS620 qui reçoit onze travaux à la file, dont certains
   changent de forme de papier, s'arrête au premier ;
3. **l'opérateur ne pouvait plus rien arrêter** : ce que Windows a pris lui appartient.

**Ce qu'on fait maintenant**, avant chaque page :

| Situation | Décision |
| --- | --- |
| machine en panne, en pause, hors ligne | on s'arrête, l'enveloppe part en attente |
| plus de `PlafondEnFile` (3) pages en file | on patiente, on relit |
| file sous le plafond | la page part |
| file illisible (pilote avare, Print to PDF) | on imprime d'un trait, comme avant |

Et **on attend que la file se vide** avant de déclarer l'enveloppe imprimée : sans cette
attente, une commande de six cents photos passait « imprimée » cinq secondes après le
premier tirage.

Quatre points à ne pas défaire :

1. **le point de reprise compte ce qui est SORTI**, pas ce qui a été remis
   (`PagesSorties` = remises − file). Le nom du champ `PrintResumePoint.PagesRemises` est
   resté pour les fichiers déjà sur les disques ;
2. **une file illisible rend `PagesEnFile = -1`**, et `PagesSorties` s'en tient alors aux
   pages remises. Soustraire un nombre négatif annoncerait plus de sorties qu'il n'en est
   parti, et la reprise **sauterait une photo** — défaut attrapé par un essai ;
3. **c'est le temps SANS PROGRÈS qui déclenche l'abandon**, jamais la durée totale : une
   commande de six cents photos passe légitimement une heure dans cette boucle ;
4. **la reprise REFAIT la dernière photo sortie** (`CadenceSpouleur.ReprendreA`). Quand une
   machine s'arrête faute d'encre ou de ruban, celle qui était en cours sort pâle ou à
   moitié, et rien ne permet de le savoir depuis le logiciel. Une feuille refaite coûte
   quelques centimes ; une photo ratée au milieu d'un paquet de six cents coûte le paquet.

Vérifié par `CadenceSpouleurTests`, sans imprimante.

---

## Deux produits ne doivent jamais être indiscernables

Le catalogue de la boutique porte **deux produits nommés « 10x15 »** : `10x15`
(102 × 152, minilab) et `10x15-dnp` (105 × 156, DS620). Triés par surface, leurs deux
tuiles se suivent dans la grille des formats, et seule une ligne de texte gris les
distinguait. La commande 04-024 du 04/08/2026 est partie sur la DNP alors qu'elle visait le
minilab : onze tirages, une seule photo sortie, et la machine bloquée derrière.

Deux garde-fous, aux deux moments où l'on peut encore se reprendre :

- **la tuile de format** porte la machine en PASTILLE colorée, plus en petit texte —
  minilab en bleu, sublimation en violet, Epson en vert ;
- **la grille des photos** affiche « Sortie : … » de la même couleur, à côté du produit, et
  **masque le choix de machine du minilab** quand le produit n'y va pas — il ne commandait
  rien et laissait croire le contraire.

La vraie correction serait de renommer l'un des deux produits au catalogue. Elle appartient
à l'exploitant, pas au code : le nom d'un produit est ce qu'il annonce au client.

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

### Le bouton « Accueil » : une sortie qui ne perd rien

Dans l'en-tête, à côté de « Retour », donc visible depuis TOUS les écrans. Il met de côté
ce qui est en préparation, puis revient à l'accueil.

- **Le travail est cherché dans toute la PILE de navigation**, pas sur le seul écran
  affiché (`Reprises.Trouver`, `Navigator.Ecrans`) : depuis le recadrage d'une photo, c'est
  la grille qui porte la commande, deux écrans plus bas.
- **Les écrans qui savent se mettre de côté implémentent `ITravailReprenable`** :
  `PhotoGridView` et `IdPhotoView`. Ailleurs, le bouton revient simplement à l'accueil — il
  n'y a rien à garder.
- **Il ramène TOUJOURS à l'accueil**, même si l'enregistrement échoue : c'est sa promesse,
  et un opérateur coincé sur un écran parce qu'un fichier ne s'écrit pas serait le pire des
  deux maux. L'échec va au journal.
- **Rien n'est annoncé à l'écran** : l'accueil montre déjà le bandeau « En attente » avec la
  commande et son heure, sous les yeux de celui qui vient d'appuyer. Une boîte de dialogue à
  chaque retour serait un clic de plus, cinquante fois par jour.
- **Une grille vide n'est pas mise de côté** : ouvrir un dossier sans rien y faire ne doit
  pas déposer une ligne « 0 photo » à chaque passage.
- **Le mode borne n'a ni « Retour » ni « Accueil »** : le parcours client est verrouillé.

### Une planche d'identité se met de côté aussi

`TravailEnAttente.Identite` (`IdentiteEnAttente`) : la norme visée, les chemins imposés par
l'écran de sélection, la photo affichée, et le travail de chaque photo
(`PhotoIdentiteEnAttente` — cadrage, repères de crâne et de menton, visage détecté, axe,
redressement, corrections, noir et blanc, fond blanc, photos par planche, planches).

- **La norme est enregistrée À PLAT**, pas par son nom : le référentiel compte 274
  documents et peut être rechargé entre-temps, alors que les cotes ne bougent pas. C'est
  aussi ce qui permet de reprendre une planche dont la norme aurait disparu.
- **`PhotoIdentiteEnAttente` est une classe à part de `PhotoEnAttente`** : une photo
  d'identité n'a ni produit, ni finition, ni découpe, et porte en revanche des repères de
  visage qui n'ont aucun sens sur un tirage. Les mêler ferait un objet dont la moitié des
  champs serait toujours vide.
- **Une planche se reprend dans l'écran d'IDENTITÉ** (`HomeView.OnAttenteReprendre`
  branche sur `travail.Identite`). La rouvrir dans la grille des tirages donnerait un cadre
  libre — pas de gabarit, pas de repères — c'est-à-dire précisément ce qui ne permet pas de
  faire une photo d'identité.
- **`StripItem.Prete` est reposé avec le reste**, avant toute ouverture de photo : c'est lui
  qui empêche la détection de visage de se relancer et d'écraser les repères qu'on vient de
  reprendre.
- **L'entrée est soldée à l'impression**, par `IdSheetRecapView` (paramètre `attenteId`) :
  la laisser ferait proposer « Reprendre » sur une planche déjà tirée, et on la tirerait
  deux fois.

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
sur le système »). C'est `config/wifi.json` qui fait vivre le code — et il l'emporte toujours
sur la lecture automatique. Celle-ci ne sert que sur un portable.

**Il se remplit dans Paramètres**, et non plus à la main : nom du réseau, clé, sécurité,
nom masqué, avec l'aperçu du code à côté. Un fichier JSON qu'il faut connaître pour le
trouver n'existe pas, du point de vue de celui qui installe un second poste opérateur — et
c'est bien ce qui s'est passé.

Deux points du formulaire :

- **la clé est en clair**, et c'est voulu : c'est une clé qu'on donne aux clients, affichée
  au mur dans la plupart des boutiques. La masquer obligerait à la ressaisir en aveugle
  pour corriger une faute de frappe ;
- **l'aperçu se fabrique sur ce qui est À L'ÉCRAN**, avant enregistrement : on scanne avec
  son propre téléphone et l'on sait tout de suite si le code marche, plutôt que de le
  découvrir devant un client. Même raison que le message d'essai du courriel.

`AppServices.SaveWifi` remplace l'objet en mémoire et pas seulement le fichier :
`PhoneUploadView` lit `Services.Wifi` à chaque affichage, et sans cela le code aurait gardé
l'ancien réseau jusqu'au prochain lancement.

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

## Un message d'erreur ne se coupe jamais

`PrintBannerText` portait `TextTrimming="CharacterEllipsis"` et `MaxWidth="620"` ; les deux
textes du bandeau des machines aussi. Or c'est la FIN qui compte : un motif de panne du
minilab tient en une phrase dont on perdait la moitié.

Les trois passent en `TextWrapping="Wrap"`. La largeur maximale reste — le bandeau ne doit
pas manger le titre de l'écran — mais c'est la HAUTEUR qui s'adapte désormais.

**Règle : aucun texte qui porte un diagnostic ne doit être tronqué.** Une étiquette, un nom
de fichier, un libellé de produit, oui. Un message d'erreur, jamais.

Le bandeau passe aussi en ALERTE dès qu'un tirage est refusé, sans attendre la fin de la
commande : le fond vert « tout va bien » sur une machine qui refuse est exactement ce qui
fait rater un incident.

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
   repeindrait aussi le contenu des `ListBoxItem`, qui gardent le fond CLAIR du système :
   on troquerait du noir sur noir contre du blanc sur blanc. Le jour où on voudra vraiment
   un style implicite, il faudra habiller les listes d'abord — les menus déroulants, eux,
   le sont désormais (ci-dessous).

---

## Les menus déroulants sont habillés, et le style est IMPLICITE

`App.xaml` porte un `Style TargetType="ComboBox"` et un `Style TargetType="ComboBoxItem"`
sans clé : les vingt-neuf listes de l'application en héritent sans qu'on ait à les toucher
une à une. Fond `FieldBrush #232D38`, liseré qui passe à l'accent au survol et à
l'ouverture, chevron accentué, liste déroulante sur `CardBrush` bordée d'accent, survol et
sélection en accent.

**Piège à connaître : un style NOMMÉ ne reprend pas le style implicite.** Les dix-sept
listes de `LargeFormatPrintView` passent par un style local `Cbo` ; sans
`BasedOn="{StaticResource {x:Type ComboBox}}"`, elles seraient restées grises au milieu
d'un écran sombre. Tout nouveau style de `ComboBox` doit porter ce `BasedOn`.

Trois détails du gabarit qui ont une raison, et que seul l'écran a révélés :

- **le popup DOIT s'appeler `PART_Popup`.** `ComboBox` le cherche par ce nom, et c'est ce
  qui déclenche le calcul de `SelectionBoxItemTemplate`. Sous un autre nom, la ligne fermée
  retombe sur le `ToString()` de l'objet : la liste des papiers de l'écran identité
  affichait « ProductChoice { Product = Studio.Core.Domain.Product, Capacite = 8, … } » au
  lieu du libellé de `DisplayMemberPath` ;
- **le libellé vit dans un `Border` qui réserve la place du chevron** (30 px). Poser
  simplement la flèche à droite dans la grille ne suffit pas : le texte passe dessous, et
  « WPA / WPA2 / WPA3 (le cas courant) » recouvrait purement et simplement le chevron ;
- la liste déroulante est un `Popup` à fenêtre transparente : son fond est porté par le
  `Border` intérieur, faute de quoi les coins arrondis laisseraient voir l'écran.

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

---

## Le parcours identité : TROIS écrans, et l'état vit dans la photo

`IdPhotoPickerView` (choisir) → `IdPhotoView` (cadrer) → `IdSheetRecapView` (récapituler,
puis imprimer). Refonte du 04/08/2026, sur le modèle de DiLand que les opérateurs
connaissent.

**Ce que l'écran unique coûtait.** Il recevait le dossier entier — 455 photos sur la carte
de l'atelier — dans une bande de 240 pixels, et n'en traitait qu'UNE : changer de photo
remettait à neutre repères, cadrage, corrections et redressement. Deux personnes d'une
même famille donnaient donc deux commandes, deux tickets et deux passages en caisse.

Quatre règles à ne pas défaire :

1. **Le travail vit dans `StripItem`, pas dans les champs de l'écran.** L'écran DÉPOSE
   (`SauverDansLaPhoto`) avant de changer de photo et REPREND (`ReprendreDeLaPhoto`)
   ensuite. Sans le dépôt préalable, on perd ce qu'on vient de régler sur celle qu'on
   quitte — c'était exactement le défaut d'avant.

2. **`StripItem.Prete` empêche la re-détection.** Rouvrir une photo relancerait la
   détection de visage et écraserait le placement manuel que l'opérateur venait de
   corriger. Les repères ne se posent qu'à la PREMIÈRE ouverture.

3. **`_enReprise` fait taire les gestionnaires des deux cases.** Reposer « fond blanc »
   pendant une reprise relancerait un détourage de quatre secondes, alors qu'on l'ordonne
   nous-même juste après.

4. **L'aperçu du récapitulatif part de la VIGNETTE, jamais de l'original.** Une planche
   pleine résolution prend une quinzaine de secondes sur 24 Mpx (journal du 04/08/2026), et
   il en faudrait autant par photo du lot. Le cadrage est en coordonnées relatives : il
   tombe au même endroit quelle que soit la définition. Le TIRAGE, lui, repart toujours de
   l'original. La résolution d'aperçu (150 ppp) est **revérifiée par
   `IdSheetRecapView.PppDApercu`** : elle se compte en pixels entiers, et montrer sept
   cases là où huit sortiront serait pire que pas d'aperçu — c'est le compte qu'on vient
   vérifier.

**Le parcours part de DEUX tuiles** — l'accueil et « type de produit » — et sa suite
d'écrans était recopiée mot pour mot aux deux endroits. Ajouter l'écran de sélection n'en a
donc d'abord corrigé qu'une moitié : par l'accueil, celui qu'on utilise réellement en
boutique, on tombait toujours sur le cadrage avec les 455 photos. Il vit désormais dans
`ParcoursIdentite.Ouvrir()`. **Les BOUTONS se doublent ; ce qu'ils font, non.**

**Un `WrapPanel` replie ses enfants DIRECTS, un par un.** Les trois boutons d'un compteur
posés à plat s'y coupaient en deux lignes, et le « + » des Planches s'affichait seul sous
la liste des papiers. Chaque réglage de la barre est donc un `StackPanel` : un groupe se
replie entier ou pas du tout.

---

## Correction d'exposition par produit : l'écart de la MACHINE

`Product.PrintExposure`, en diaphragmes, ajoutée au rendu par
`PrintOrchestrator.AvecLaCorrectionDuProduit`.

⚠ **Aucun produit ne l'utilise.** Elle avait été posée à +0,25 sur `ID-FR-6` et
`e-photo-dnp` le 04/08/2026, la DS620 sortant plus sombre que le minilab sur le même
fichier ; l'exploitant l'a fait annuler le jour même, sur tirages. Les deux produits sont
donc revenus à **0**, dans le catalogue de la boutique comme dans celui du dépôt.

Le mécanisme reste en place et réglable à la fiche produit : l'écart de machine est réel,
et il se remesurera sur des tirages plutôt que dans le code. Ce qui suit vaut pour le jour
où on le remettra.

Trois points :

1. **C'est un réglage de PRODUIT, pas une constante.** L'écart dépend de la machine et du
   papier, et se corrige au dixième de diaphragme après avoir regardé un tirage réel.
   Champ « Exposition à l'impression » de la fiche produit, borné à ±2 IL.
2. **Elle ne touche pas l'aperçu de cadrage.** L'opérateur cadre sur ce qu'il voit ; une
   photo éclaircie à l'écran l'amènerait à la rassombrir à la main. Le récapitulatif des
   planches, lui, l'applique — c'est le dernier regard avant le papier.
3. **Une COPIE des réglages, jamais l'objet de l'article.** Celui-ci appartient à la
   commande enregistrée : l'y ajouter ferait s'empiler la correction à chaque
   réimpression, et la troisième sortirait délavée. Vérifié par `PrintExposureTests`.

---

## Minilab : la machine se choisit sur le ROULEAU, pas sur son rang

`ChoisirSelonLeRouleau` examine les machines prêtes, la machine par défaut d'abord, et
retient la PREMIÈRE dont le rouleau porte le format.

Le DE100 de la boutique compte deux machines, et elles n'ont jamais le même rouleau — c'est
tout l'intérêt d'en avoir deux (relevé du 04/08/2026 : **A = 152 mm Glossy, B = 210 mm
Lustre**). Prendre la première prête revenait à ignorer la seconde : un 21×29,7 était refusé
« le rouleau chargé dans la machine A fait 152 mm » alors que le rouleau de 210 était monté
à côté. Commandes 04-010 et 04-014, deux feuilles et deux clients.

Quatre règles :

1. **La machine par défaut est examinée en premier** — à format égal, rien ne change, et un
   10×15 ne se met pas à sortir de l'autre machine parce qu'elle porte un rouleau plus large.
2. **Un choix IMPOSÉ ne se discute pas** (barre de la grille, ou `MinilabMachineId` du
   produit) : l'opérateur seul sait quel rouleau il vient de monter. Le refus nomme alors la
   machine voisine qui porterait le format, s'il y en a une
   (`MachineQuiPorteLeFormat`) — sans quoi il envoie changer un rouleau qui tourne déjà à
   deux mètres.
3. **Une machine qui ne répond pas est sautée** ; son silence n'est retenu que si AUCUNE ne
   répond — sinon une machine endormie mettrait la commande en attente à côté d'une machine
   prête.
4. **Si aucun rouleau ne convient, on rend le premier qui a répondu**, pour
   qu'`EnsurePaperFits` nomme la machine et le rouleau à charger. Rendre « rien » ferait
   perdre la seule explication utile.

Vérifié par `MinilabRoutingTests`, sans minilab.

⚠ **La règle ne vaut que si personne n'a imposé de machine — et c'est là qu'était le
défaut.** La barre de la grille (`PhotoGridView.LoadMachinesAsync`) posait
`SelectedIndex = 0` à chaque ouverture, ce qui IMPOSAIT la machine A sans que personne ne
l'ait demandé : la règle 2 court-circuitait alors tout le reste, et le 21×29,7 restait
refusé même après la correction du choix par rouleau (commandes 04-010, 04-014 et 04-019
du 04/08/2026). La liste porte donc une première ligne **« Automatique — selon le
rouleau »**, retenue par défaut, qui remet `PreferredMinilabMachine` à `null`.

**Règle à tenir :** `PreferredMinilabMachine` ne doit être renseignée que sur un geste
EXPLICITE de l'opérateur. Toute pose automatique de cette propriété rouvre le défaut.

---

## Combien de temps ça va encore prendre

`EstimationDuree`, affichée dans le bandeau : « Commande 04-045 — 12 / 24 photos sorties ·
environ 5 minutes ».

Le bandeau ne disait que le compte. L'opérateur qui a un client devant lui pose une autre
question — ai-je le temps d'en servir un autre ? — et elle n'a qu'une réponse : une durée.

Trois choses à ne pas défaire :

1. **La commande en cours se chronomètre ELLE-MÊME dès trois photos sorties.** La cadence
   du moment vaut mieux que n'importe quelle moyenne : la machine peut être froide,
   occupée, ou en pleine maintenance. En deçà de trois, on prend le débit appris sur ce
   format, et à défaut la valeur par défaut de sa longueur.
2. **Les maintenances sont DEDANS**, et on ne les modélise pas séparément. Le DE100
   s'interrompt pour nettoyer sa tête et avancer son papier ; ces pauses font partie de
   l'attente. Le débit étant mesuré de bout en bout, elles y sont comprises — les exclure
   donnerait une estimation toujours trop courte, c'est-à-dire la pire des deux.
3. **La précision affichée doit correspondre à la précision réelle.** Personne n'annonce
   « 4 minutes 37 » : on dit « environ 5 minutes ». Au-delà de dix minutes on arrondit à
   cinq, au-delà d'une heure au quart d'heure. « environ » disparaît quand le débit a été
   mesuré sur assez de tirages.

Le débit s'apprend commande par commande (`config/debits.json`), en moyenne **pondérée par
le nombre de tirages** — une commande de soixante photos pèse plus qu'une commande d'une
seule, qui est presque entièrement faite du réveil de la machine. Les valeurs aberrantes
sont rejetées, et le compte retenu est borné pour que la moyenne reste GLISSANTE : une
machine qui ralentit doit continuer d'être suivie.

⚠ **Le bandeau a besoin d'un battement de 5 s** : `DureeRestante` se calcule à la lecture
et rien ne la notifie d'elle-même. Sans lui, l'affichage resterait figé entre deux photos —
vingt secondes sur un A4.

Vérifié par `EstimationDureeTests`.

---

## Ce qui reste à tirer : TROIS comptes, on retient le plus petit

`EstimationConsommables` — papier, encre, bac de maintenance — pour le format qu'on
s'apprête à tirer.

Le bandeau annonçait « ~576 × 10x15 » d'après le seul papier. Sur les machines de la
boutique, ce chiffre était un mensonge : le bac de maintenance de la A est à 95 %, le
magenta de la B à 15 %. Un opérateur qui lance trois cents tirages sur cette annonce se
retrouve à mi-parcours avec une machine à l'arrêt et un client devant lui.

Trois règles :

1. **le résultat dit toujours ce qui LIMITE** — c'est la seule chose qui dise quoi
   préparer. « ~120 × A5 · magenta à 15 % » ;
2. **une encre basse se dit même quand elle ne limite pas encore** (seuil 20 %) : c'est le
   moment de commander la cartouche, pas celui de la changer ;
3. **le bac de maintenance se REMPLIT** : ce qui reste, c'est ce qui manque pour arriver à
   100.

**L'estimation porte sur le format VISÉ** — `PrintOrchestrator.DernierFormatMinilab`, celui
de la dernière enveloppe partie. Annoncer des 10×15 à quelqu'un qui lance des A4 ne lui
apprend rien.

### La calibration s'apprend, elle n'est pas écrite dans le code

Convertir un pourcentage d'encre en tirages dépend de la machine, du format et de ce qu'on
imprime : un fond noir vide une cartouche bien plus vite qu'un portrait sur fond blanc.
Aucune constante ne serait juste.

`Apprendre` compare deux relevés (compteur + niveaux, pris à chaque rafraîchissement du
bandeau, sans coût supplémentaire) et en déduit la consommation réelle, rangée dans
`config/consommables.json`. Trois garde-fous :

- **au moins 50 tirages d'écart** : le pourcentage est un entier, et sur dix tirages son
  arrondi fausserait le calcul d'un facteur deux ;
- **le niveau doit avoir BAISSÉ** : une cartouche qu'on vient de changer remonte, et
  l'écart n'a alors aucun sens ;
- tant que rien n'a été observé, l'estimation est marquée APPROXIMATIVE et s'affiche avec
  un tilde. Les valeurs par défaut sont volontairement PRUDENTES : faire changer une
  cartouche un peu tôt coûte quelques euros, laisser une commande de trois cents tirages
  s'arrêter au milieu coûte un client.

Vérifié par `EstimationConsommablesTests`.

---

## Prévenir le client : un message qui ne livre RIEN

`PhotoMailer.PrevenirCommandePrete` — aucune pièce jointe, et c'est tout le sujet. Joindre
les photos reviendrait à les donner sans les vendre : l'envoi des fichiers est une
prestation à part, facturée, qui passe par `Envoyer`.

Même voie SMTP que le reste (`Expedier`) : un serveur qui accepte l'un accepte l'autre, et
les refus se traduisent au même endroit.

**L'aperçu et l'envoi partagent la même méthode** (`ApercuCommandePrete` délègue à
`CorpsCommandePrete`). Deux textes entretenus séparément finiraient par différer, et
l'opérateur relirait autre chose que ce qui part chez le client.

Le contrôle d'adresse est volontairement grossier — une arobase, un point après elle :
valider une adresse pour de bon est impossible sans lui écrire, et un contrôle tatillon
refuserait des adresses valides.

### Le message peut partir TOUT SEUL

Bouton « 🔔 Prévenir à la fin » de l'écran des tirages : l'adresse se prend AVANT
d'imprimer, pendant que le client est encore au comptoir, et le message part quand la
machine a fini. L'opérateur n'a rien à surveiller — c'est tout l'intérêt.

Quatre règles :

1. **À la fin du TIRAGE, pas de l'envoi.** Sur le minilab, envoyer trente tirages prend
   quelques secondes et les sortir prend plusieurs minutes. Annoncer « c'est prêt » à
   l'envoi ferait venir le client devant une machine qui travaille encore. Sur les circuits
   sans accusé de sortie — spouleur, DNP — on prévient à la remise, c'est le mieux qu'on
   puisse promettre de ce côté ;
2. **jamais si un tirage a échoué** : on ne fait pas venir quelqu'un pour une commande
   incomplète ;
3. **une seule fois** (`Order.CustomerNotified`) : une réimpression n'enverra pas un second
   message — deux courriels pour la même commande font douter le client de ce qu'il doit
   venir chercher ;
4. **l'adresse voyage avec la COMMANDE** (`Order.CustomerEmail`), pas avec l'écran : le
   message part bien après que celui-ci a été quitté, et une commande enregistrée survit à
   un redémarrage.

Un courriel qui ne part pas ne ressemble jamais à un échec d'impression : le tirage est
sorti, c'est ce qui compte, et le message se rattrape depuis « Commandes du jour ».

---

## Le relais : sa sortie d'erreur DOIT être drainée

`De100BridgeClient` redirige `StandardError` du relais. **Il faut alors la lire en
continu**, sans quoi le relais se bloque : il écrit son journal ligne par ligne sur
`Console.Error`, et le tampon d'un tube anonyme fait quelques kilo-octets. Une fois plein,
`WriteLine` suspend le processus enfant.

Vingt-sept redémarrages le 04/08/2026, et ce seul défaut expliquait quatre pannes
apparemment sans rapport : une commande sans verdict, le bandeau des machines qui se vide,
l'actualisation qui traîne, et les compteurs de tirages qui ne montent plus alors que les
photos sortent.

**Règle : rediriger un flux sans le drainer est un interblocage différé.** Si l'on ajoute un
jour `RedirectStandardOutput`, il faudra le drainer aussi.

Effet de bord recherché : le journal du relais arrive dans `app-*.log`, préfixé
« relais · ». C'est le seul endroit d'où l'on voit ce qui se passe côté SDK. Les deux côtés
sont en UTF-8 — `Console.OutputEncoding` chez le relais, `StandardErrorEncoding` chez le
client — sinon les accents arrivent en « trouvâ€š ».

---

## Une lecture ratée ne fait jamais disparaître une machine

`MachineBarView.RefreshAsync` relit chaque famille de son côté et garde son DERNIER ÉTAT
CONNU. La barre est recomposée des deux à la fin.

Avant, une seule liste était remplie par les deux lectures successives : quand le minilab
échouait, sa part restait vide et la DNP écrasait l'ensemble. **Les deux DE100
disparaissaient du bandeau** dès que le relais toussait.

Une machine qu'on ne joint pas pendant dix secondes n'a pas disparu de la boutique.
L'opérateur a besoin de la voir — ne serait-ce que pour savoir qu'elle existe.

---

## Vider une file d'impression Windows

`DnpSpouleur.Vider` supprime les travaux **un par un, par leur chemin**.

`Win32_Printer.CancelAllJobs` semble plus direct mais échoue avec « Operation is not valid
due to the current state of the object » dès que l'instance vient d'une requête à
propriétés restreintes (`new SelectQuery("Win32_Printer", null, ["Name"])`) : sans son
chemin complet, WMI refuse d'invoquer une méthode dessus.

La suppression travail par travail a deux avantages : elle marche quand un seul travail est
fautif, et elle dit combien elle en a réellement retiré.

⚠ **Le bouton du bandeau retrouve sa tuile par son `DataContext`**, jamais par sa lettre
dans `_tuiles` : la barre peut avoir été recomposée entre l'affichage et le clic, et la
recherche échouait alors en silence.

---

## Chaque état de machine a une CONDUITE

`ConduiteMachine` : pour chaque état des deux familles, ce que Studio fait et ce que
l'opérateur doit faire. Les états étaient traduits en français à trois endroits —
`DnpStatus.Message`, `DnpSpouleur.Decrire`, `De100JobTracker.Describe` — et **aucun ne
disait quoi faire**. « Intervention nécessaire » n'a jamais dit à personne quoi toucher.

Cinq conduites : `Continuer`, `Patienter`, `MettreEnAttente`, `ViderLaFile`, `Arreter`.

Deux règles :

1. **Tout état tombe dans l'une des cinq.** Un état inconnu vaut `MettreEnAttente` : on
   préfère faire patienter une commande à tort que la déclarer perdue ;
2. **quand la machine DIT quelque chose, on le répète mot pour mot.** Le SDK Fuji donne ses
   messages en anglais et ils sont explicites — « Cartridge cover (left) open. Close the
   cartridge cover (left). » C'est aussi ce que l'opérateur lit sur l'écran de la machine.

⚠ **Le cas « file figée »** (`ViderLaFile`) est le seul où la machine MENT : elle se
déclare prête, ne signale aucune erreur, et rien ne sort. Il l'emporte donc sur tout ce
qu'elle raconte. Seuil : `MinutesAvantDeViderLaFile` = 10.

Vérifié par `ConduiteMachineTests`.

---

## Minilab : le MOTIF d'un refus, lu à la source

`PIF_GetPrintInfo(handle, indice, …)` remplit une `ST_PRINT_INFO` dont le champ `errmsg`
porte **512 caractères de message**. La fonction était déclarée depuis le premier jour et
n'était **appelée nulle part** : le motif existait, personne n'allait le chercher.

Conséquence : un tirage refusé ne disait que « erreur signalée par le minilab ». Le 21×29,7
des commandes 04-015, 04-020 et 04-027 du 04/08/2026 a échoué **trois fois** sans laisser
la moindre piste — y compris après le changement de la cartouche de cyan, ce qui a écarté
l'hypothèse des consommables sans rien apporter à sa place.

`De100Driver.OnOrderCallback` relit donc les tirages d'une commande en `Error` et joint
leur motif au verdict (`De100JobTracker.Raison`). Deux règles :

1. **le motif COMPLÈTE le statut, il ne le remplace pas** : le statut situe le moment,
   le motif dit la cause ;
2. **les doublons sont écartés** — sur trente tirages refusés pour la même raison, la
   répéter trente fois ne dit rien de plus.

⚠ **Les deux callbacks natifs sont entièrement protégés** (`try`/`catch`). Ils sont appelés
par le SDK : une exception qui remonte jusqu'à lui ne se rattrape nulle part et emporte le
processus — donc le relais, donc le suivi de TOUS les tirages en cours. Ne jamais retirer
ces gardes, et n'y appeler que du code qui ne lève pas.

### L'image envoyée doit avoir TROIS CANAUX

**C'est la cause du 21×29,7, cherchée une journée entière.** Le DE100 refuse les images en
niveaux de gris, et il le fait comme tout le reste : sans un mot, dix secondes après avoir
accepté la commande.

Un scan noir et blanc — ou une photo passée en noir et blanc — traverse tout le rendu en
gardant son unique canal. Le paramètre `ColorSpace` que Studio envoie au SDK vaut pourtant
« 1 » (RGB) depuis toujours : l'image doit lui correspondre.

Prouvé en renvoyant **le fichier même que Studio avait produit**, converti en sRGB et rien
d'autre : sorti du premier coup. Ce qui a mis sur la voie, c'est la comparaison des deux
PNG — même définition, même densité, mais `Gray / 2 canaux` d'un côté et
`sRGB / 4 canaux` de l'autre.

`PrintOrchestrator.EnTroisCanaux` est appelée sur toute image partant au minilab, et
`FitPageToRoll` réécrit désormais le fichier **même quand sa taille est déjà juste** si
elle est en gris.

⚠ **Le define PNG est indispensable** : poser `ColorSpace` et `ColorType` ne suffit pas. Le
format PNG réécrit en niveaux de gris dès que tous les pixels le sont — son optimisation
automatique — et il faut `color-type 2` pour l'interdire. Deux tentatives de correction ont
échoué là-dessus. `MinilabImageTests` verrouille le point, y compris le test qui échouerait
si l'on retirait le define en « simplifiant ».

⚠ **On n'appelle plus le SDK depuis une callback du SDK.** `PIF_GetPrintInfo` y était lu
pour récupérer `errmsg` : l'indice 0 rend `BadParam` — le SDK compte à partir de 1 — et
surtout **le relais mourait quelques secondes après l'appel** (« Pipe is broken »,
commande 04-041). Le callback se contente désormais de ce que `ST_ORDER_INFO` porte déjà :
format, cotes et compte des sorties. Ses cotes sont en **dixièmes de millimètre**.

### La DÉFINITION de l'image : c'est la MACHINE qui la dicte

**C'est la cause du 21×29,7 refusé, trouvée par essais sur la machine le 04/08/2026.**

`PIF_DevGetPixelCount` dit ce que le DE100 attend pour un format donné, et ce n'est PAS ce
qu'on calcule :

| Format | Notre calcul | Ce que la machine réclame | Écart |
| --- | --- | --- | --- |
| 210 × 297 mm à 300 ppp | 2480 × 3508 px | **2515 × 3543 px** | +35 px, soit +3 mm |

Elle ajoute son **débord** : 2515 px = 212,9 mm, 3543 px = 299,9 mm. Elle veut l'image AVEC
les 3 mm qu'elle rognera.

`PrintOrchestrator.DefinitionAttendue` la lui demande donc, et `FitPageToRoll` cale l'image
dessus. Repli sur notre calcul si elle n'en dit rien — machine muette, relais coupé, format
inconnu : **on ne perd jamais un tirage parce qu'une lecture a échoué.**

⚠ **Pourquoi le 18×24 sortait, lui, avec exactement le même écart** : il passe par un canal
VARIABLE (`21xL`), qui tolère l'à-peu-près. Le 210 × 297 tombe sur le canal FIXE `A4`, qui
exige la définition au pixel près — et refuse **sans donner le moindre motif**.

Neuf essais de NOMS de format (« 210x297 », « A4 », « 21xL », cotes inversées, sans nom…)
ont tous échoué avant qu'on regarde de ce côté. **Le nom n'y était pour rien** ; le
`MinilabPrintSizeName` des produits 210 × 297 est donc revenu à vide. Ce qui a mis sur la
voie : le TÉMOIN de la série d'essais — le format du 18×24, envoyé avec une image au
mauvais rapport — a échoué lui aussi, alors qu'il sort tous les jours.

Deux outils écrits pour cette enquête, à garder :

- `DeviceProbe de100 essais <machine> <image>` — fait varier un paramètre d'envoi à la fois
  et s'arrête au premier qui sort. **Un refus ne coûte pas de papier** : le compteur de la
  machine et le métrage restant ne bougent pas, c'est vérifiable ;
- `DeviceProbe de100 definitions <machine> <image>` — fait varier la DÉFINITION, en
  redimensionnant l'image à ce que `PIF_DevGetPixelCount` réclame. C'est lui qui a tranché,
  au premier essai.

### Les canaux du DE100 : variables ou FIXES

C'est la clé du refus du 21×29,7, et elle vient de la base de DiLand
(`OutputProfileChannel`, cotes en unités 96 ppp). Pour un rouleau de 210 mm :

| Canal | Longueur admise | Rouleau | Variable |
| --- | --- | --- | --- |
| `21xS` | 50 → 210 mm | 210 | oui |
| `21xL` | 210 → 1000 mm | 210 | oui |
| **`A4`** | **297 mm exactement** | 210 | **NON** |
| `A5` | 210 mm | 148 | non |

Studio fabrique le `PrintSizeName` d'après le rouleau : « 210x240 » pour un 18×24,
« 210x297 » pour un 21×29,7. Le premier sort, le second est refusé — **même machine, même
rouleau, même code, six fois de suite**. La différence : 240 ne correspond à aucun canal
fixe et tombe dans le variable `21xL` ; 297 correspond exactement au canal FIXE `A4`, que
la machine exige alors par SON NOM.

D'où `MinilabPrintSizeName = "A4"` sur les trois produits 210 × 297 du catalogue.

⚠ **Les autres rouleaux ont les mêmes canaux fixes**, et donc le même piège en attente :

| Rouleau | Canaux FIXES (longueur) |
| --- | --- |
| 102 | 10x10 (102), 10x13 (127), 10x15 (152), 10x20 (203) |
| 127 | 13x9 (89), 13x13 (127), 13x15 (152), 13x17 (170), 13x18 (180), 13x19 (190), 13x20 (203), 13x26 (254) |
| 152 | 15x15 (152), 15x20 (203), 15x23 (228), 15x30 (304), 15x40 (400) |
| 203 | 20x20 (203), 20x25 (256), 20x27 (273), 20x30 (307), 20x40 (400) |
| 210 | **A4 (297)** |

**Rien n'a été changé pour eux**, et c'est délibéré : on ne corrige que ce qui est cassé, et
tout ce que la boutique tire aujourd'hui sort. Mais si un format se met à être refusé sans
motif, **c'est la première chose à regarder** — le remède est le champ « Nom du format au
minilab » de la fiche produit, à remplir avec le nom du canal ci-dessus.

### Quand la machine ne dit RIEN

C'est le cas du 21×29,7 : `errmsg` vide, aucun événement machine, refus dix secondes après
une acceptation. On journalise alors ce que la COMMANDE porte — format demandé, cotes,
`printedNum`/`orderNum` — et le verdict de `PIF_GetPrintInfo` lui-même, qui est déjà une
information.

`DeviceProbe de100 formats` interroge la machine en lecture seule et a permis d'écarter les
consommables, la longueur maximale de tirage (1000 mm), la file, la résolution et le calcul
de pixels du SDK. **Il reste le `PrintSizeName`** — le seul paramètre qui dépende de la
configuration de la machine.

⚠ **`PIF_DevGetPixelCount` n'est PAS un validateur de format.** Il accepte 297 × 210 sur un
rouleau de 152 : il calcule des pixels, il ne juge rien. Il apprend en revanche que la
machine attend l'image AVEC son débord — 2515 × 3543 px pour un 210 × 297, soit 213 × 300 mm
— là où Studio envoie 2480 × 3508. L'écart de 3 mm existe aussi sur les 10×15 qui sortent
tous les jours : **ce n'est pas la cause du refus**, et il ne faut pas partir dessus.

Le nom du format se règle à la fiche produit (`Product.MinilabPrintSizeName`, champ « Nom
du format au minilab »). Vide = déduit du rouleau, ce qui convient à tout ce que la boutique
tire aujourd'hui.

---

## Minilab : ce que la machine dit d'elle-même

`De100BridgePrinter.MachineEvent` — bourrage, fin de rouleau, encre épuisée. Le relais
transmettait ces événements depuis toujours **sans un seul abonné** : un tirage refusé se
lisait « Minilab : tirage 04-020-1-001 — ÉCHEC · erreur signalée par le minilab », point
final, alors que la machine venait d'en donner le motif. C'est ce qui a rendu l'échec des
commandes 04-015 et 04-020 du 04/08/2026 inexplicable après coup.

`AppServices` s'y abonne : tout va au journal, et seul ce qui ARRIVE (`IsActive`) et qui
est grave (`SystemError`, `Error`) monte au bandeau. Un événement qui se TERMINE est une
panne réparée — l'annoncer ferait clignoter une alerte pour une bonne nouvelle.

---

## Le DÉBORD du minilab se remplit d'IMAGE, jamais de blanc

`PrintOrchestrator.RemplirLeDebord`, appelée par `FitPageToRoll`.

Le DE100 réclame l'image AVEC les 3 mm qu'il rognera (voir « La DÉFINITION de l'image :
c'est la MACHINE qui la dicte »). On étendait le canevas à cette définition en comblant de
BLANC : **toutes les photos sortaient cernées d'un liseré d'un millimètre et demi**, sur
tous les formats, et le rognage de la machine ne le mangeait pas — il part du bord du
PAPIER, pas du bord de l'image. Constaté en boutique le 05/08/2026 sur des 10×15, des 13×18
et des 15×20 ; c'est la contrepartie non vue de la correction du 21×29,7.

Le débord est le même en PIXELS sur tous les formats — +35 px par axe à 300 ppp, relevé
dans les journaux du jour sur trois formats — donc pas la même proportion en largeur et en
hauteur.

| Format demandé | Notre calcul | Ce que la machine réclame |
| --- | --- | --- |
| 152 × 102 mm | 1795 × 1205 px | **1830 × 1240 px** |
| 152 × 80 mm | 1795 × 945 px | **1830 × 980 px** |
| 152 × 180 mm | 1795 × 2126 px | **1830 × 2161 px** |

Trois règles :

1. **Le calage se fait en DEUX temps.** D'abord les cotes NUES du tirage : c'est là, et
   seulement là, que du blanc a le droit d'entrer — un 10×15 posé sur un rouleau de 210 mm
   laisse une bande de chaque côté, et elle est VOULUE (`MinilabPrintSize` rend toujours la
   largeur du rouleau). Ensuite le débord, par agrandissement.
2. **Un seul facteur pour les deux axes**, le plus exigeant, et l'on rogne l'excédent de
   l'autre. Cadrer chaque axe séparément étirerait la photo de quelques millièmes — une
   déformation sur toute l'image contre quelques pixels sur un bord.
3. **Sans débord, on ne touche à rien.** Machine muette, format inconnu, repli sur notre
   calcul : la cible vaut les cotes nues et l'image passe telle quelle.

Vérifié par `DebordMinilabTests`, dont un essai qui garantit que les bandes blanches
légitimes du rouleau SURVIVENT à la correction.

---

## L'« Angle » d'une borne n'est pas la rotation du client

`DiLandImporter.QuartsDeTourResiduels`, pour les DEUX parcours (`Stage` et `Import`).

**C'est la rotation TOTALE depuis le fichier brut, orientation EXIF comprise.** Studio,
lui, applique toujours l'EXIF d'abord (`ImagePipeline.RenderInto` appelle `AutoOrient`) puis
les quarts de tour : reprendre l'angle tel quel les ADDITIONNAIT. Une photo de téléphone en
portrait — EXIF 8, donc Angle 270 — était redressée par l'EXIF puis tournée de 270° de
plus. Elle partait couchée, et le recadrage du client, exprimé lui aussi dans le repère
redressé, tombait à côté. C'est ce qui restait après les corrections de cadrage des passes
précédentes.

Relevé sur la base de la boutique le 05/08/2026, sur les 185 photos d'angle non nul :

| Cas | Nombre | Ce que c'est |
| --- | --- | --- |
| Angle = orientation EXIF | 183 | l'EXIF, et rien d'autre |
| Angle sans EXIF | 2 | une VRAIE rotation faite à la borne |

D'où la soustraction, et non l'abandon pur et simple de l'angle : les ignorer ferait sortir
ces deux-là de travers.

Quatre points :

1. **L'orientation se lit sur la COPIE**, jamais sur l'original : DiLand passe au XOR les
   1024 premiers octets des commandes traitées, c'est-à-dire l'en-tête EXIF lui-même. Les
   deux parcours recopient avant de lire, et c'est pour cela que ça marche.
2. **Le recadrage n'est PAS transposé.** DiLand l'exprime dans le repère REDRESSÉ — celui
   où l'image se retrouve une fois la rotation juste appliquée. Corriger l'angle suffit ;
   transposer le rectangle en plus le ferait tomber deux fois à côté.
3. **L'invariant qui le vérifie** : après cette rotation, les côtés de l'image doivent
   tomber sur les `Width` × `Height` notés par DiLand. Il tient sur les 185 cas.
4. **Toute anomalie de lecture rend « déjà droite »** (`OrientationExif`) : un fichier
   tronqué, un PNG, une photo sans EXIF ne doivent pas empêcher une commande de s'ouvrir —
   et c'est exactement le comportement d'avant ce lecteur.

Vérifié par `OrientationBorneTests`, sur les cotes réelles d'un téléphone (4000 × 6016).

---

## Le bandeau compte des FEUILLES, la fin de commande compte des VERDICTS

Les deux ne coïncident pas sur le minilab : une photo demandée en deux exemplaires part en
UN tirage de deux copies (`PrintNum`), et le DE100 rendra donc **un seul verdict pour deux
feuilles**. Le bandeau annonçait « 1 / 1 » pendant que la machine sortait deux photos.

- `PrintProgress.Total` et `TravailImpression.Sortis` sont en FEUILLES — c'est ce que
  l'opérateur attend de voir tomber dans le bac ;
- `PrintProgress.Verdicts` et `TirageTermine` comptent les RÉPONSES — attendre autant de
  réponses que de feuilles laisserait la commande affichée jusqu'au délai de garde de 35 min.

### L'avancement se lit sur le COMPTEUR de la machine

`De100JobTracker.Report` ne rend une issue que sur un statut DÉFINITIF. Tant que la commande
est `Printing`, **aucun verdict n'arrive** : la barre restait à « 0 / 30 » pendant plusieurs
minutes avant de sauter à « 30 / 30 ». Ce n'était pas « parfois », c'était toujours.

`SuiviImpressions` relève donc `TotalPrintCount` au début du tirage puis toutes les 10 s :
lui monte feuille par feuille. Trois règles :

1. **Toutes les 10 s, pas toutes les 5** : chaque relevé traverse le relais 32 bits pendant
   que la machine travaille, et une 10×15 met une dizaine de secondes à sortir. Interroger
   plus vite ne montrerait rien de plus.
2. **Borné au total et jamais décroissant** : le compteur est global à la machine — une
   commande lancée à côté depuis DiLand ne doit pas faire dépasser cent pour cent.
3. **Dégradation propre** : sans compteur lisible, l'affichage retombe sur les verdicts,
   comme avant. Un relais muet ne laisse jamais la barre plate.

---

## Le rouleau choisi survit à la navigation

`PhotoGridView.LoadMachinesAsync` remettait `PreferredMinilabMachine` à null et la liste sur
« Automatique » à CHAQUE ouverture de l'écran : un aller-retour suffisait à perdre le
rouleau qu'on venait de désigner.

Le choix explicite de l'opérateur est donc RELU avant de reposer les lignes — poser
l'ItemsSource déclenche `OnMachineChanged`, qui l'écraserait. Une machine passée hors ligne
entre-temps retombe sur « Automatique », préférence comprise : imposer une machine absente
ferait refuser la commande en nommant une machine éteinte.

⚠ La règle de la 13ᵉ passe tient toujours : `PreferredMinilabMachine` ne doit être
renseignée que sur un geste EXPLICITE. **Restaurer un choix explicite antérieur EN EST UN** ;
la poser d'office à l'ouverture, non.

---

## Retirer une photo : « − » à un exemplaire

Le bouton s'arrêtait à 1 sans rien faire de plus, et il fallait deviner qu'on décochait la
case pour retirer une photo. Descendre jusqu'à zéro est le geste naturel, et c'était déjà
celui de la touche 0 (`SetQuantityOnTargets`).

La quantité est laissée à 1 et non à 0 : si l'opérateur recoche la photo, elle revient à un
exemplaire. Le bouton de la vignette et le raccourci clavier font la même chose — sans quoi
la touche et le bouton ne se comporteraient pas pareil.

## Maj+clic : la plage COCHE, elle ne bascule pas

`PhotoGridView.SelectionnerLaPlage`. Basculer chaque photo de la plage décocherait celles
qui étaient déjà prises : sur une plage qui en recouvre une autre, l'opérateur perdrait son
travail au lieu de l'étendre.

L'ancre suit le dernier clic SIMPLE, coché ou décoché. Un Maj+clic sans ancre ne fait rien —
il ne doit pas prendre toutes les photos depuis le début du dossier. L'ordre est celui de la
GRILLE (`_photos`), qui n'est pas celui du disque : les photos se présentent de la plus
récente à la plus ancienne, ou par nom si l'opérateur a trié.

---

## Le relais 32 bits mourait de sérialiser une énumération

`De100Protocol`, constructeur statique.

**C'est la cause du « Pipe is broken » d'une impression sur deux** (05/08/2026), et il
fallait rouvrir l'application pour repartir. Ce n'était pas l'impression qui cassait le
relais : **le relais était déjà mort quand elle arrivait.**

```
16:47:19  relais · Fatal error. Internal CLR error. (0x80131506)
             at System.Text.Json...EnumConverter`1[[...DnpStatusGroup]]..ctor(...)
             at System.Activator.CreateInstance(Type, Object[])
16:47:39  Impression : commande 05-020 lancée en tâche de fond
16:47:40  Impression : commande 05-020 en échec | Pipe is broken
```

Le relais répond à chaque commande dans son propre `Task.Run`. Le bandeau demande l'état du
minilab puis celui des DNP coup sur coup : les deux réponses se sérialisaient en même temps
et faisaient construire le MÊME convertisseur d'énumération par réflexion. En 32 bits, le
moteur d'exécution n'y survit pas.

Deux mesures, et il faut les deux :

1. **Tous les convertisseurs du protocole sont résolus au chargement de la classe**, donc
   une seule fois et sur un seul fil. Le cache est chaud avant le premier message, et plus
   personne n'en construit en pleine course. `MakeReadOnly` ferme la porte derrière : une
   mutation ultérieure lèverait au lieu de rouvrir la course en silence.
2. **Les propriétés CALCULÉES ne traversent plus le tube** (`[JsonIgnore]` sur `DnpStatus`).
   Le protocole transporte la donnée brute, jamais ses interprétations : tout se recalcule à
   l'arrivée depuis `Raw`.

⚠ Un type qui échapperait à la liste du préchauffage retomberait sur la résolution
paresseuse — donc sur la course. **Tout nouveau type transporté doit y être ajouté.**

---

## Le format ne se pose plus TOUT SEUL sur les photos cochées

Une commande de borne arrive avec un format PAR PHOTO — le client en choisit plusieurs dans
la même commande (relevé le 05/08/2026 : 10x15+10x10, 10x15+13x18, 10x15+15x20, 8x10+10x15,
13x18+18x24…). `AppliquerLeCadrageDeLaBorne` les pose bien, un par un.

Mais le seul fait de dérouler la liste « Produit » du bandeau les ramenait toutes au même :
`OnDefaultProductChanged` écrivait sur toutes les photos cochées. **Le multi-format ne
survivait pas à la sélection.**

Le report est donc passé sur un BOUTON — « Appliquer aux photos cochées » — où il se voit
et ne part pas tout seul (décision de l'exploitant, 05/08/2026). Trois règles :

1. **La liste ne modifie plus rien à elle seule.** Elle ne fait que désigner le format que
   prendront les photos cochées ENSUITE, et celui des photos qui n'en ont pas encore.
2. **Le bouton porte sur les photos COCHÉES**, pas sur toutes : c'est ainsi qu'on passe
   cinq photos en 15×20 en laissant les dix autres en 10×15.
3. **Il reste éteint sans photo cochée** : un bouton qui ne fait rien laisse croire que le
   format n'a pas été pris.

### Dans « Modifier », on vise à la CASE, plus au Ctrl+clic

Le Ctrl+clic sur la bande faisait déjà exactement cela, et personne ne l'a trouvé — rien ne
l'annonçait. Chaque vignette porte donc une case à cocher, liée à `PhotoItem.Ciblee`, et le
Ctrl+clic continue de fonctionner pour qui le connaît.

La vignette affiche AUSSI son format (`ProductLabel`) : sans lui, rien ne distingue à l'œil
une 10×15 d'une 15×20 dans la bande, et le multi-format se pilotait à l'aveugle.

---

## Les contrôles qui portent leur couleur EUX-MÊMES

Pendant du « noir sur noir » des `TextBlock`, et signalé de la même façon : « il y a encore
certains textes en noir sur fond bleu » (05/08/2026). Le bleu, ce sont nos panneaux —
`CardBrush #1E2731`, `PanelBrush #2A3440`.

**WPF donne aux `CheckBox`, `RadioButton`, `TextBox` et `PasswordBox` la couleur de texte du
SYSTÈME**, c'est-à-dire du noir, et un fond blanc aux deux derniers. Une vingtaine de
libellés étaient donc illisibles : « Activer l'envoi par courriel », les trois choix de
détourage, « Contour de découpe », « Noir et blanc »…

Contrairement aux `TextBlock`, cela se corrige à UN endroit : ces types tiennent leur
couleur de `Control.Foreground`, que rien ne leur transmet depuis le conteneur. `App.xaml`
porte donc quatre styles IMPLICITES.

**Sans le danger du style implicite de `TextBlock`** (qui repeindrait le contenu des listes
au fond clair du système) : ces quatre-là ne paraissent jamais dans une liste.

Trois détails qui ont leur raison :

1. **`CaretBrush`** : sans lui le curseur de saisie est noir sur fond sombre, donc invisible ;
2. **`SelectionTextBrush`** : sans lui le texte sélectionné reste noir sous le bleu de la
   sélection — le défaut d'origine, en plus petit ;
3. **les styles NOMMÉS reprennent l'implicite** (`BasedOn="{StaticResource {x:Type TextBox}}"`).
   C'est le piège déjà rencontré sur les listes déroulantes : un style nommé ne reprend
   jamais l'implicite, et les quatre champs habillés localement seraient restés blancs.

Vérifié par `ContrasteXamlTests`, **et à l'écran** : le XAML décrit une intention, pas un
résultat.

---

## « Commandes du jour » montre les photos

Un numéro et une heure ne disent rien d'une planche d'identité. Quand un client revient
chercher la sienne, l'opérateur ouvrait les commandes une à une pour la retrouver.

Chaque ligne porte donc les quatre premières photos de la commande, en vignettes de 76 px,
suivies d'un « +12 » quand il y en a plus — sans quoi on croirait avoir tout sous les yeux.

Trois règles :

1. **Les vignettes arrivent APRÈS l'affichage** (`ChargerLesApercusAsync`). Lire celles de
   toutes les commandes de la semaine avant de montrer la liste la ferait attendre pour
   rien ; le `ThumbnailService` a déjà son cache pour les fois suivantes.
2. **Un fichier manquant n'est pas une anomalie** : au-delà de trente jours les photos
   partent à l'archive et la commande reste affichée. On saute la vignette, la ligne garde
   son numéro, sa date et ses boutons.
3. **Aucune boîte de dialogue ici**, contrairement à `DossierDesPhotos` : celle-ci prévient
   l'opérateur qui a DEMANDÉ quelque chose. Un aperçu, personne ne l'a demandé — et une
   alerte par commande archivée rendrait l'écran inutilisable.

`OrderRow` notifie lui-même (`INotifyPropertyChanged`) plutôt que d'hériter
d'`ObservableObject` : un `record` ne peut hériter que d'un autre `record`.

---

## La vignette dit le FORMAT, et le rapport des côtés

Depuis qu'une commande mélange les formats, c'est la seule chose qui distingue à l'œil une
10×15 d'une 15×20 dans la planche — et sans elle le multi-format se pilote à l'aveugle.

- **`FormatLabel` est sans PRIX**, contrairement à `ProductLabel` du bandeau : répété sur
  soixante vignettes il mange la place, et le total est déjà dans la barre du bas ;
- **le badge disparaît quand aucun format n'est posé** : un badge vide ne dit rien ;
- **le rapport des côtés** (`RatioLabel`, déjà présent dans la planche) accompagne le format
  dans « Modifier » : on voit du même coup ce que le format va rogner.

---

## Donner l'application à un collègue : ce qui était écrit en dur

Trois valeurs suffisaient à rendre l'application inutilisable ailleurs qu'en boutique, et
aucune ne le disait :

| Ce qui était en dur | Où | Ce qui se passait ailleurs |
| --- | --- | --- |
| `C:\Program Files (x86)\DiLand Studio 2\…` | `DiLandRepository.DefaultRoot` | plus une seule commande de borne |
| `SC-P800` cherché dans le nom | `LargeFormatPrintView` | l'écran s'ouvrait sur le télécopieur |
| le relais cherché dans `bin\Debug\` | `De100BridgeClient` | le minilab ne répond pas |

Trois réponses, et la même règle pour les trois : **on détecte, et le réglage du poste
l'emporte quand il est renseigné.**

- `DiLandLocator` — le processus DiLand s'il tourne, les deux « Program Files », les racines
  des disques. On ne balaie jamais un disque en profondeur : des minutes au démarrage pour
  un gain nul.
- `DetectionImprimantes` — par FAMILLE (« SureColor », « DS », « CP »), jamais par modèle.
  Une boutique ne rachète pas la même référence. ⚠ **On ne retient jamais une marque
  entière** : le Canon iR-ADV du magasin est un photocopieur, et proposer un agrandissement
  dessus ferait perdre un tirage.
- `PosteSettings` (`config\poste.json`) — tout facultatif. Vide, l'application se débrouille ;
  renseigné, l'opérateur a le dernier mot. C'est le filet pour la machine qu'on n'a pas
  prévue, et aucune liste de motifs ne sera jamais complète.

---

## Le rapport de diagnostic : ce qu'il emporte, et ce qu'il n'emportera jamais

`RapportDiagnostic`, appelé depuis les paramètres.

Les journaux ont trouvé le liseré blanc et le « Pipe is broken ». Sur le poste d'un
collègue, personne ne va les lire : le défaut est signalé au téléphone, en mots.

**Rien ne part tout seul.** C'est un geste — un bouton, quand ça ne va pas — et le rapport
dit ce qu'il contient avant de partir. Le poste travaille sur les photos de clients.

⚠ **Le filtre `EstSensible` est le point à ne pas défaire.** `mail.json` porte le mot de
passe d'application de la boîte du magasin, `dropbox.json` le jeton d'accès, `wifi.json` la
clé du réseau : les joindre les enverrait en clair, par courriel, à chaque rapport. **En cas
de doute sur un nom de fichier, on écarte.**

Trois détails qui ont leur raison :

1. **Les journaux sont tronqués à 2 Mo, par la FIN** : c'est là qu'est ce qui vient de se
   passer, et un serveur de courriel refuse couramment au-delà de 25 Mo ;
2. **le journal du jour est ouvert en partage** (`FileShare.ReadWrite`) : il est tenu par
   l'application elle-même, et sans cela le rapport échouerait sur le seul fichier qui
   compte ;
3. **un second bouton écrit le fichier sans courriel** — un poste dont l'envoi n'est pas
   configuré est justement celui dont on a le plus besoin des journaux.

Vérifié par `RapportDiagnosticTests`, **et sur les vraies données** du poste
(`tools\Studio.RapportProbe`, qui cherche le mot de passe réel dans l'archive produite).

---

## La mise à jour : on annonce, on n'installe pas

`MiseAJour` lit la dernière publication du dépôt par l'API GitHub.

**Ce qui n'est jamais automatique, c'est l'INSTALLATION.** Elle ferme l'application —
peut-être au milieu d'une commande, devant un client. La vérification, elle, ne coûte
qu'une requête.

Quatre refus, tous délibérés :

1. **brouillon ou préversion** : les envoyer ferait tirer sur du code qu'on n'a pas fini ;
2. **publication sans archive** : rien à installer, et le dire vaut mieux que de proposer
   une mise à jour qui échouerait au téléchargement ;
3. **version égale** : republier pour corriger une description ne doit pas proposer une
   réinstallation à tous les postes — la comparaison est STRICTEMENT supérieure ;
4. **erreur réseau** : hors ligne, quota dépassé, dépôt injoignable sont des circonstances
   ordinaires. On rend `null`, on ne lève pas, et l'on continue de travailler.

### Pourquoi un script d'installation et non une copie

Windows verrouille les fichiers d'un programme qui tourne : **l'application ne peut pas se
remplacer elle-même.** Le script attend sa fermeture, recopie, puis la relance. Il arrête
aussi le relais du minilab, qui tient les mêmes DLL et survit parfois à l'application.

`robocopy` sans purge : les données du poste ne sont pas dans le dossier d'installation,
mais un profil ICC ou un DEVMODE déposé à la main s'y trouve peut-être.

### Publier

`tools\Publier.ps1`. Version AUTONOME (`--self-contained`) : le collègue n'installe aucun
runtime. L'archive fait ~240 Mo, et c'est le prix à payer pour qu'elle s'installe partout.

⚠ **Le relais 32 bits est publié dans `de100\`**, et pas ailleurs : c'est le sous-dossier
où `De100BridgeClient.ProbeHostPaths` le cherche. Publié ailleurs, l'application ne le
trouverait qu'en remontant vers une sortie de compilation qui n'existe pas chez un collègue.

⚠ **Monter `<Version>` dans `Directory.Build.props` avant de publier.** Le script refuse une
version déjà parue — sinon on croirait avoir livré, et aucun poste ne verrait rien.

---

## Le rendu décode le JPEG à la taille du TIRAGE, pas à celle du fichier

`ImagePipeline.LectureEconome`, appelée par `Render`.

Le décodeur JPEG sait rendre l'image au demi, au quart ou au huitième en sautant des
coefficients : c'est du sous-échantillonnage exact, pas une réduction après coup.
`ThumbnailService` s'en servait déjà pour les vignettes ; le rendu des tirages, non — il
lisait vingt-quatre mégapixels pour n'en garder que deux dixièmes.

Mesuré le 05/08/2026 sur la photo de la commande 05-026 (6016 × 4000, cellule 413 × 531) :

| Rendu | Avant | Après |
| --- | --- | --- |
| planche d'identité (redressée) | 2587 ms | **1696 ms** |
| 10×15 ordinaire | 1216 ms | **815 ms** |

Trois règles, et chacune a coûté une mesure :

1. **On demande un CARRÉ, du plus grand des deux côtés.** L'indication porte sur le
   FICHIER, dont l'orientation n'est connue qu'après lecture de l'EXIF : demander
   1194 × 1796 sur un fichier couché ferait décoder trop petit, et le tirage y perdrait
   vraiment. Un carré est juste dans les deux sens et coûte au pire un cran de décodage.
2. **Le sur-échantillonnage (× 2) ne se justifie QUE devant un redressement.** Sans lui, la
   source va directement à sa taille finale par un seul rééchantillonnage — garder deux fois
   les pixels nécessaires n'y sert à rien. C'est pourtant le cas le plus fréquent de la
   boutique : le 10×15 y a gagné un tiers de son temps. La marge qui reste (1,3) couvre le
   rognage au rapport.
3. **Jamais à la hausse.** Le décodeur ne sait pas agrandir : un besoin supérieur au fichier
   le laisse simplement le lire en entier, ce qui est exactement ce qu'un agrandissement
   demande.

⚠ **La qualité est FIGÉE par des essais** (`RenduEconomeTests`) : le rendu économe est
comparé au rendu pleine résolution, et l'écart RMS doit rester sous 0,02. Sur la vraie
photo : 0,00209 pour la cellule d'identité, 0,00302 pour le 10×15 — cinq fois moins que le
0,0096 déjà accepté pour la réduction avant redressement.

⚠ **Ces essais mesurent sur des formes PLEINES, jamais sur une mire de traits fins.** Une
mire d'un ou deux pixels est un cas d'aliasing qu'aucune photo ne présente : elle faisait
échouer l'essai à 0,036 pour une réduction que la vraie photo traverse à 0,002. On mesure
ce qu'on imprime.

### Ce qui a été mesuré et laissé tel quel

**Le redressement fin coûte 1017 ms sur 2 Mpx, et c'est irréductible.** Quatre voies
essayées — `Rotate` tel quel, alpha désactivé, sans virtual pixels, `Distort
ScaleRotateTranslate` — toutes entre 1010 et 1020 ms. Ce Magick.NET est bâti sans OpenMP :
seul le nombre de pixels compte, et `ReduireAvantRedressement` l'a déjà ramené au minimum.
Abaisser le sur-échantillonnage sous 2 gagnerait quelques centaines de millisecondes contre
de la qualité sur un tirage qu'on vend : non.

**Le fil de l'interface ne bloque nulle part** : pas un `.Result`, `.Wait()`,
`GetAwaiter().GetResult()` ni `Thread.Sleep` dans `Studio.App`. La lenteur ressentie venait
des rendus, qui tournent déjà en tâche de fond.

`tools\Studio.RenduProbe` refait toutes ces mesures sur une vraie photo, y compris le
contrôle de qualité. À relancer avant de toucher au pipeline.

## Un doublon doit recevoir la vignette ET la définition de son original

`PhotoGridView.DupliquerPhoto` fabrique une seconde ligne de commande sur le même fichier.
Deux choses n'arrivent d'ordinaire qu'à la LECTURE du dossier, que le doublon a manquée, et
il faut donc les lui recopier à la main :

- **la vignette source** (`SetSourceThumbnail`) — sans elle `RefreshThumbnail` sort
  immédiatement, et le doublon s'affiche comme une case vide dans la planche comme dans la
  bande de « Modifier ». Le bouton passait pour mort ;
- **la définition du fichier** (`SetSourceSize`) — sans elle `PhotoItem.Cadre` rend `null`,
  faute de savoir sur combien de pixels bâtir le cadre. Le doublon partait alors à
  l'impression en **pleine image**, sans le cadrage de son original.

L'ordre compte : la définition d'abord, puis la vignette, et le cadrage d'origine
(`PoserLeCadrageDOrigine`) AVANT les deux — c'est lui que le cadre relit en naissant.

Côté impression, rien à faire : `OrderService.CreateOrder` copie le fichier une seule fois
mais crée bien **deux `OrderItem`**, chacun avec son format et son cadrage.

## `PhotoItem.Cle` : le chemin ne suffit plus à ranger un cache

Depuis qu'une même photo peut figurer deux fois dans une commande, tout cache rangé par
`Path` confond l'original et son doublon. C'est ce qui arrivait au cache d'aperçu de
`EditSelectionView` (`_photosPretes`, qui garde la photo composée AVEC ses corrections) :
l'original passé en noir et blanc rendait son image au doublon resté en couleur.

`PhotoItem.Cle` est propre à l'instance et ne change jamais. Règle : **ce qui dépend des
RÉGLAGES se range par `Cle`, ce qui ne dépend que du FICHIER se range par `Path`.** Le
cache haute définition (`_hautesDefinitions`) reste donc par chemin, et c'est voulu — un
doublon profite du chargement de son original.

## Le contour de découpe existe AUSSI en « remplir le format »

Il n'y était pas, et la case était grisée dans ce mode — celui par défaut de presque tous
les produits. L'opérateur cliquait, rien ne se cochait, la case passait pour cassée
(signalé le 06/08/2026). Trois choses le composaient, corrigées ensemble :

1. `ImagePipeline.RenderInto` ne posait `aDecouper` que dans la branche « photo entière » ;
   en « remplir », le bord de la photo EST le bord du tirage, et c'est encore là que passent
   les ciseaux quand plusieurs tirages sortent sur la même feuille ;
2. `EditSelectionView` grisait la case hors de `Fit` et `Polaroid` ;
3. la case montrait l'état de la photo AFFICHÉE alors que le clic porte sur les photos
   VISÉES : sur une sélection dont la courante ne faisait pas partie, elle se remettait à
   zéro juste après avoir été cochée. Elle lit désormais `Visees()`.

## Le Polaroid porte ses traits de coupe SANS qu'on les demande

Le cadre garde ses proportions (0,823) et ne remplit donc pas la feuille : sur un 10×15
(0,67) il laisse du blanc en haut et en bas. Sans repère, le tirage ne ressemble pas à un
Polaroid mais à une photo posée au milieu du blanc — constaté sur papier le 06/08/2026.

`RenderPolaroid` trace donc toujours le contour sur `pose.Frame`, quelle que soit la case
« Contour de découpe », et pose des **repères d'angle** (`DrawCornerTicks`) dans le blanc
autour : à un millimètre du cadre, donc emportés par la chute, là où le contour reste sur
le tirage. Ceux qui ne tiennent pas sont simplement omis — sur un 10×15, le cadre occupe
toute la largeur et il ne reste de place qu'en haut et en bas.

## La bande basse : 4 mm de marge latérale, à cause du fond perdu

`SheetFooterLayout.MargeBordMm` vaut **4 mm**, quand l'air vertical n'en fait que 1,2. Ce
n'est pas une question de goût : la planche part à fond perdu, la machine réclame l'image
avec 3 mm de débord qu'elle rogne elle-même (`PrintOrchestrator.RemplirLeDebord`), soit
près d'un millimètre et demi mangé sur CHAQUE bord. À 1,2 mm, la date perdait son premier
chiffre sur le papier.

La date et l'heure sont écrites séparément — `dd/MM/yyyy` en corps 5 mm, `HH:mm` à
`FractionHeure` (0,72) et en gris. C'est la DATE qui prouve qu'une photo d'identité est
récente ; l'heure n'est qu'une précision d'atelier, et elle ne doit pas peser autant à
l'œil. Les largeurs se calculent sans contexte de dessin (`LargeurTexte`) : le peintre et
la découpe DOIVENT appeler la même fonction, sans quoi l'heure sort de sa zone.

⚠ La première ligne de la mention est en **gras et en capitales** : elle se majore à 0,68
cadratin par caractère, pas à 0,58 comme le reste. Avec 0,58, elle mordait sur le code QR.

## Ce qui n'est pas cadré n'est plus assombri

Le voile sombre a disparu des quatre endroits où il vivait : `CropSurface`,
`CropEditorView`, `IdPhotoView` et les vignettes de la planche (`DessinerCadre`). Il
noircissait précisément la partie qu'on regarde pour décider de la rattraper — un visage
qui déborde du cadre devenait illisible. Le cadre jaune, épais, dit la limite (demandé le
06/08/2026).

## Le logo est VERSIONNÉ, pas fabriqué à la compilation

`src\Studio.App\Assets\studio-photo.ico` porte six définitions (256 → 16 px).
`tools\Studio.Logo` le redessine — un diaphragme orange dans un anneau bleu, tout en
fractions du côté, si bien que le 16 px est exactement le 256 px en plus petit. Chaque
définition est tracée quatre fois trop grand puis réduite : les traits fins d'un diaphragme
tracés directement à 16 px disparaissent.

**À ne relancer que si la marque change.** Une icône refaite à chaque compilation change
d'octets sans changer de dessin, et le dépôt en garderait la trace à chaque commit.

## DS620 : pourquoi l'envoi direct à la DiLand est HORS DE PORTÉE

DiLand n'imprime pas sur la DS620 par le pilote Windows. Son journal
(`DnpPrinterQueueDocumentPrinter.log`) montre le SDK en direct :

```
GetFreeBuffer → WaitForFreeBuffer → SetMediaSize(CSP_6x4)
            → SetOvercoatFinish(OVERCOAT_FINISH_GLOSSY) → SendImageData
```

L'idée de faire pareil est morte sur un fait mesuré le 06/08/2026, DiLand ouvert :

```
DiLand ouvert : True        SDK chargeable: True
Ports trouvés : 0 []
rang 0 : 0x80000000  (comm KO) — imprimante injoignable
```

**DiLand tient le port USB en exclusif.** Le SDK ne voit pas la machine tant qu'il tourne —
et il tourne en permanence, c'est lui qui reçoit les bornes. `SendImageData` ne servirait
donc que DiLand fermé, c'est-à-dire jamais. C'est la même contrainte que celle qui a fait
écrire `DnpSpouleur` (l'état par le spouleur plutôt que par le SDK), et elle vaut aussi pour
l'impression. Décision de l'exploitant, 06/08/2026 : on garde DiLand ouvert.

⚠ **Les noms internes du `.GPD` ne disent PAS ce que le dialogue affiche.** Le pilote publie
`OPTYPE_LUSTER`, `OPTYPE_MATTE1`, `OPTYPE_FINE_MATTE`, `OPTYPE_LUSTER_MATTE` — et son
dialogue, lui, propose Brillant / Mat / Mat fin / Lustre, la file de la boutique étant sur
**Brillant** alors que son DEVMODE porte `OPTYPE_LUSTER` (copie d'écran du 06/08/2026). En
déduire une finition d'après le nom interne est donc faux, et une finition annoncée de
travers coûte la feuille : `LectureDevMode` rend le nom brut, sans le traduire.

De même, `PRINTBUFFCONTROL` s'appelle **« Réessayer l'impression »** dans le dialogue —
`PBC_NONCLEAR` = Activer. Chercher « tampon » dans les propriétés de l'imprimante ne donne
rien.

### Le dialogue du pilote ne s'ouvre JAMAIS sur le fil de l'interface

Même cause, troisième victime. Le dialogue du pilote DS620 interroge la machine pour se
remplir — il porte un onglet « Infos de l'imprimante ». DiLand tenant le port, il s'ouvre et
reste « (Ne répond pas) », le fil de l'interface bloqué DANS un appel natif dont on ne peut
pas sortir. Windows a fait ce qu'il fait d'une fenêtre qui ne pompe plus : il a fermé
l'application. **Trois fois en onze minutes le 06/08/2026** — journal des événements,
`AppHangXProcB1`, et pas la moindre exception dans le journal de Studio, puisqu'il n'y en a
pas eu.

`DevMode.ShowDriverDialogAsync` lance donc le dialogue sur son propre fil, en STA et en
arrière-plan : l'écran reste vivant, et un pilote qui ne répond jamais laisse un fil en plan
au lieu de tuer l'application. `DialoguePilote` prévient en plus quand DiLand tourne, et
laisse le dernier mot à l'opérateur.

⚠ `DevMode.ShowDriverDialog` (la version synchrone) existe toujours, et elle bloque. Elle
est réservée aux outils en ligne de commande — jamais l'application.

## On ne demande RIEN au relais sur les DNP quand DiLand tourne

Quatrième victime du même port USB, et la plus coûteuse : **elle arrêtait le minilab.**

Le bandeau demandait l'instantané DNP au relais 32 bits à chaque rafraîchissement. DiLand
tenant le port, le SDK ne peut pas répondre — mais le relais mourait avant même d'essayer, en
CONFIGURANT les types de la réponse :

```
12:29:44  relais · Fatal error. Internal CLR error. (0x80131506)
             at JsonTypeInfo`1[[System.UInt32]].CreatePropertyInfoForTypeInfo()
             at ...JsonPropertyInfo.Configure()   ← et la même chaîne, en boucle
12:29:55  Impression : commande 06-009 en échec | Pipe is broken
```

Moins d'une seconde après son démarrage, à chaque fois. Et le relais emporte le MINILAB avec
lui : plus aucun tirage DE100 ne partait.

Deux mesures, et il faut les deux :

1. **`MachineBarView` ne demande plus l'instantané DNP quand `DiLandPresence.IsRunning()`.**
   On sait déjà que la réponse serait « injoignable » ; on lit le spouleur, qui répond
   toujours. Une tuile d'état ne vaut pas une machine à l'arrêt.
2. **`De100Protocol` appelle `MakeReadOnly()` sur chaque `JsonTypeInfo`**, et pas seulement
   `GetTypeInfo`. C'est ce qui manquait au correctif du 05/08/2026 : `GetTypeInfo` fabrique
   la description du type mais laisse sa CONFIGURATION au premier usage — or c'est elle qui
   descend dans chaque propriété et fait, par réflexion et en pleine course, le travail que
   le moteur 32 bits ne supporte pas.

⚠ La leçon générale, quatre fois vérifiée le 06/08/2026 : **tout ce qui touche au port USB
de la DNP pendant que DiLand tourne finit par bloquer ou tuer quelque chose.** Le SDK direct,
le dialogue du pilote, l'instantané du relais. Avant d'ajouter un appel qui parle à cette
machine, se demander d'abord si le spouleur ne sait pas déjà répondre.

## Ce qu'un DEVMODE contient, et ce qu'il faut y surveiller

`LectureDevMode` lit — jamais n'écrit — les réglages qu'un pilote Unidrv range à la fin de
son DEVMODE : une suite de chaînes ASCII terminées par un zéro, chaque nom de réglage suivi
de l'option retenue. Ce sont exactement les noms du `.GPD` du pilote.

⚠ **On CHERCHE les noms connus, on ne compte pas les chaînes deux par deux.** Le bloc privé
réel de la DS620 s'ouvre par les marqueurs d'Unidrv (`DINU"`, `SMTJ`, `RESDLL`…) : le
découpage par paires se décale d'un cran et annonce « OPTYPE_LUSTER = PRINTBUFFCONTROL ».

⚠ **On n'écrit JAMAIS dans les octets privés d'un pilote.** Les chaînes ne sont qu'une table
de noms ; la sélection réelle vit ailleurs dans le bloc. Changer un réglage passe par le
dialogue du pilote (`DevMode.ShowDriverDialog`), et par lui seul.

Deux réglages sont signalés comme dangereux, et ce sont les deux suspects du décalage de
couleurs constaté sur les commandes 06-005 et 06-006 :

| Réglage | Valeur trouvée | Pourquoi c'est un problème |
| --- | --- | --- |
| `Resolution` | `Option1` (High-speed) | le mode où l'entraînement du papier est le plus sollicité, donc où les passages de couleur se décalent le plus. Dialogue : Graphique → « Qualité d'impression » |
| `PRINTBUFFCONTROL` | `PBC_NONCLEAR` | l'image reste en mémoire d'un tirage à l'autre : ce qui restait du précédent se voit sur le suivant, en fantôme décalé. Dialogue : Caractéristiques de l'imprimante → « Réessayer l'impression » |

Un TROISIÈME réglage compte, et il ne vit pas dans le DEVMODE : les **fonctionnalités
d'impression avancées** de la file Windows (onglet Avancé des propriétés d'imprimante).
Actives — le défaut de Windows, et le cas de la boutique — le spouleur enregistre le travail
en EMF et le REJOUE dans son propre processus pour le rendre ; le pilote reçoit alors une
image découpée en bandes, à un rythme qui ne dépend plus de nous.
`DnpSpouleur.FonctionnalitesAvancees` le lit et `PrintOrchestrator` l'écrit au journal. On ne
le change pas : il appartient au poste.

`PrintOrchestrator` écrit ces réglages au journal à chaque enveloppe : sans cela, un tirage
raté ne laisse aucune trace de ce sur quoi il est sorti.
`tools\Studio.PrintProbe devmode-lire <fichier.bin>` les dit en ligne de commande.

**Ce qui est établi, et ce qui ne l'est pas.** Les fichiers rendus des deux commandes ont
été ouverts et sont PROPRES : le défaut naît après nous. Deux tirages du même fichier à une
minute d'écart ont donné des défauts DIFFÉRENTS — toute la planche pour l'un, une bande
haute pour l'autre. Un réglage figé donnerait le même défaut à chaque fois : ce qui varie
tient donc au tampon, à la mécanique, ou aux deux. L'épreuve qui trancherait est d'imprimer
le même fichier depuis DiLand.

## Un délai dépassé n'est pas un refus : on NE se replie PAS sur le pilote

Le repli sur le pilote Windows est la sécurité de l'envoi direct à la DNP : si le chemin
direct échoue, la page part quand même, et c'est bien. **Sauf sur un délai dépassé**, où
cette sécurité fabrique exactement ce qu'elle prétend éviter — un tirage en double.

Commande 12-012, Créteil, 12/08/2026. Trois planches d'identité sur la DS620 :

```
12:25:31  relais · dnp-print lancé pour la planche 003
12:25:41  relais · « dnp-print » sans reponse en 10 s ... On repond sans attendre
12:25:41  app   · Envoi direct DNP indisponible : on passe par le pilote
12:25:41  app   · Impression « Studio 12-012-1 » sur DP-DS620      ← la MÊME planche
12:25:42  relais · Envoi direct DNP : 1 exemplaire(s) ... acceptes ← et la machine l'avait prise
12:25:42  relais · « dnp-print » a fini par repondre apres 10831 ms, trop tard
```

**Le relais ne peut pas annuler ce qu'il a lancé.** `RepondreSansJamaisBloquer` répond
« machine muette » passé le budget, mais le fil natif continue sa vie — c'est écrit dans son
propre commentaire, et c'est irréductible : le SDK ne s'interrompt pas. Un délai dépassé ne
dit donc jamais « rien n'est parti », il dit « je ne sais pas ».

Trois règles en découlent, et elles sont indépendantes :

1. **Le délai suit ce que la commande FAIT, pas la machine qu'elle vise.**
   `De100Commands.EngageLaMachine` en tient la liste — `submit`, `cancel`, `dnp-print` — et
   le relais comme le client s'y réfèrent. Ils en tenaient chacun une, elles ont divergé, et
   `dnp-print` ne figurait dans aucune des deux : un envoi de papier se voyait accorder les
   dix secondes d'une simple question. **Ne jamais réécrire cette liste en dur quelque part.**
2. **Une DNP qui imprime répond lentement.** Ce n'est pas une panne, c'est son
   fonctionnement — le commentaire de `EtatDesDnp` le disait déjà pour l'instantané. Mesuré
   le 12/08/2026 : 8 457 ms à Maisons-Alfort et 10 831 ms à Créteil, pour la même planche
   arrivant derrière deux autres. Toute commande de trois tirages ou plus est concernée.
3. **`TimeoutException` sur `DnpPrintAsync` lève `PrintUnconfirmedException`** — jamais un
   repli, jamais une mise en attente. L'enveloppe reste `Spooled`, c'est-à-dire « partie,
   sortie non confirmée » : `PendingPrintQueue` ne la reprend pas toute seule, et la
   réimprimer réclame un geste explicite. L'opérateur regarde le bac ; lui seul peut dire ce
   qui est sorti.

⚠ Le délai dépassé sur l'INTERROGATION qui précède (`dnp-snapshot`), lui, se replie
normalement : à ce stade aucune image n'a été remise à la machine. La frontière est l'appel
`DnpPrintAsync` lui-même, et c'est pour cela qu'il porte son propre `try`.

C'est la règle que le minilab observait déjà — « rien n'est renvoyé automatiquement quand la
sortie est douteuse », voir `SubmitTimeout` et `Expire`. Le circuit DNP ne la suivait pas.

## Détourage : une panne de mémoire ne condamne ni la séance, ni le réseau

Le fond était parfait au cadrage et ne l'était plus au récapitulatif des planches. Créteil,
12/08/2026. Rien dans le rendu n'était en cause.

**Pourquoi le récapitulatif refait le calcul.** Le masque est mis en cache sous l'empreinte
`{photo}|{largeur}x{hauteur}` (`MasqueSujet`). L'aperçu du cadrage et la planche rendue à la
taille d'impression n'ont pas les mêmes dimensions : ce sont deux entrées de cache
différentes, donc **deux passages du réseau**. Le premier tenait, le second manquait de
mémoire vidéo. Ce n'est pas un défaut du cache — deux tailles donnent deux masques — mais
c'est ce qui fait du récapitulatif l'écran où la panne se voit.

**Ce que le journal montrait, et qui disait tout :**

```
13:23:39  BiRefNet : échec du détourage (... DmlFusedNode_0_0 ...) — repli sur la méthode par couleur.
13:23:48  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
13:23:55  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
13:23:58  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
```

Une vraie panne, puis des `Fail` secs en rafale : **la session morte n'était pas jetée**.
Une seule panne de mémoire condamnait la séance entière, cadrage compris, jusqu'au
redémarrage de l'application.

Trois règles, désormais :

1. **Une exécution ratée jette la session** (`EcarterEtReessayer`). Un CHARGEMENT raté, lui,
   ne se retente toujours pas — recharger 490 Mo à chaque photo pour échouer pareil
   bloquerait le comptoir. Ce sont deux échecs différents.
2. **On ne retombe sur la couleur qu'en dernier recours.** Une panne de mémoire ne dit rien
   du réseau, elle dit que CE modèle est trop gros pour cette carte : le modèle fautif est
   écarté pour la séance et l'on repart sur le « lite », dont le contour reste sans commune
   mesure avec la règle par couleur. `CalculerMasque` fait deux tours au plus.
3. **L'opérateur l'apprend** (`DernierRepli`, affiché par `IdSheetRecapView`). Le repli
   n'était visible que du journal, et l'écran ne montrait rien d'anormal — seulement un fond
   moins propre qu'à l'écran d'avant.

⚠ **Le seuil de mémoire vidéo était trop bas, et la comparaison est stricte.**
`MemoireVideoMinimaleGo` valait 6 ; la GTX 1660 SUPER de Créteil annonce
`qwMemorySize = 6 442 450 944`, soit **6,0 Go tout ronds**. `6 < 6` étant faux, le modèle
puissant lui était offert — et il a échoué. Le seuil est passé à 8.

⚠ **Griser un choix n'efface pas un réglage déjà enregistré.** `detourage.json` gardait
`ModelePuissant: true`. `AppServices.ModelePourCetteCarte` convertit donc la demande en
« lite » quand la carte est sous le seuil, plutôt que de laisser le poste gâcher une planche
chaque matin pour un verdict connu d'avance. Une carte qui n'annonce pas sa mémoire garde le
bénéfice du doute.

⚠ **WMI ment sur la mémoire vidéo.** `Win32_VideoController.AdapterRAM` est un `uint32` : il
rend « 4 Go » pour toute carte de 4 Go ou plus. C'est ce chiffre tronqué qui a d'abord fait
croire à une carte de 4 Go à Créteil. `CarteGraphique` lit le registre pour cette raison —
ne pas revenir à WMI.

## La reprise du catalogue se juge sur les PRODUITS, pas sur le contour de la liste

Un poste neuf démarre sur cinq produits d'amorçage qui pointent tous sur
« Microsoft Print to PDF ». `CatalogueLivre.PoserSiAbsent` les remplace par le catalogue de
la boutique — encore faut-il qu'il les reconnaisse.

**La reconnaissance portait sur l'ensemble EXACT des codes** (`SetEquals`), et c'était trop
strict d'un cran : elle se désarmait au premier produit ajouté, c'est-à-dire au premier geste
d'un poste neuf. Le poste `DESKTOP-KT88VDM` l'a payé — découvert le 12/08/2026 :

```
2026-08-12 13:04:01 | Impression « Studio 12-001-1 » sur Microsoft Print to PDF :
                      demandé 102×152 mm, page obtenue 102×152 mm
```

Son opérateur avait dupliqué un 30×40 pour se faire un 40×50. Un produit de plus, et la
reprise ne l'a plus jamais reconnu : ses cinq produits d'amorçage sont restés des semaines,
**toutes ses commandes partaient dans un PDF**, et il cherchait la panne du côté de sa DNP et
de son DE100 — qui étaient parfaitement détectés, relais et SDK compris (`PIF_Open` en
164 ms).

**La règle, désormais** : les cinq produits d'amorçage sont-ils tous là et **intacts** —
même imprimante, mêmes cotes ? Alors personne ne s'en est servi pour imprimer, quoi qu'on
ait ajouté à côté. Le prix et le nom ne comptent pas ; **l'imprimante et les cotes, si** :
on ne configure pas un poste sans toucher à l'une des deux.

La limite qui protège un vrai catalogue reste entière, et elle est mieux placée : **dès
qu'un de ces cinq produits vise une vraie machine**, l'exploitant a configuré son poste et
le fichier lui appartient.

⚠ **Ce qui a été ajouté à côté est CONSERVÉ** (`ProduitsAjoutes`). Le 40×50 fabriqué à la
main est un vrai travail ; le perdre à la faveur d'une reprise serait payer la correction du
prix d'un dégât, et l'exploitant n'aurait aucune raison de faire le lien. L'ancien fichier
reste par ailleurs à côté, en `products.amorcage-<horodatage>.json`.

⚠ **La reprise emporte aussi les `devmode-*.bin` du catalogue livré**, et ceux-là NE SONT PAS
PORTABLES (voir la note sur les index de format papier). Un poste repris hérite donc du
`dmPaperSize` de Maisons-Alfort, qui n'existe pas forcément chez lui : à contrôler sur le
premier tirage. C'est le prix, connu, de la reprise automatique — et l'argument de plus pour
résoudre un jour le format papier par son NOM.

## Le rapport de diagnostic emporte le catalogue

`products.json` vit dans `catalog\`, pas dans `config\` : il ne partait donc pas. Or c'est LE
fichier qui décide où une commande s'imprime. Sans lui, le catalogue de `DESKTOP-KT88VDM` a
dû être déduit d'une taille de rendu au journal — 1205 × 1795 px, soit 102 × 152 mm, soit
exactement le produit d'amorçage.

Les prix y figurent : ce sont ceux de la boutique qui envoie le rapport, elle les connaît, et
ils ne concernent aucun client. La règle qui ne bouge pas reste celle des secrets —
`mail.json` et `dropbox.json` ne partent jamais.

## Un profil couleur manquant ne coûte que les couleurs, jamais le tirage

`ImagePipeline.Profil` levait. Une `FileNotFoundException` sur un `.icc` remontait donc tout
le rendu et emportait la commande entière — pas un tirage moins juste : **aucun tirage**.

Le poste `DESKTOP-KT88VDM` y a perdu son après-midi du 12/08/2026. Son catalogue venait
d'être repris et réclamait `DS620-R0.icc` ; son pilote DNP avait installé le même profil sous
le nom `PD_DS620-R0.icc`. Une lettre de préfixe, et plus rien ne sortait :

```
Impression : commande 12-002 en échec | System.IO.FileNotFoundException:
  Could not find file '...\catalog\icc\DS620-R0.icc'.
```

Deux règles en sortent :

1. **La gestion des couleurs est un raffinement ; le tirage est le métier.** Profil absent ou
   illisible → on tire en sRGB présumé, on l'écrit au journal, et la commande part. C'est ce
   que faisait le logiciel avant que les profils existent. Les DEUX points d'appel sont
   concernés — le rendu courant et le chemin Polaroid.
2. **Le null est mémorisé dans le cache.** `ConcurrentDictionary` ne mémorise pas une absence
   de valeur : sans un `ColorProfile?` porté explicitement, une enveloppe de quarante photos
   relirait le disque quarante fois pour rater le même fichier, au milieu du rendu.

⚠ **Le nom d'un profil n'est pas stable d'un pilote à l'autre.** Même fichier, deux noms :

| Poste | Fichier posé par le pilote | Taille |
|---|---|---|
| Maisons-Alfort | `DS620-R0.icc` | 1 674 792 |
| DESKTOP-KT88VDM | `PD_DS620-R0.icc` | 1 674 796 |

`TrouverLeProfil` accepte donc un **préfixe de fabricant**, à condition qu'il se termine par
`_` ou `-` : `PD_DS620-R0.icc` convient, `MONDS620-R0.icc` non. La limite compte — rapprocher
deux profils sans rapport ferait sortir les couleurs d'une autre machine, et c'est le genre
de défaut que personne ne voit venir.

## Le format papier d'un DEVMODE se résout par son NOM

Note qui REMPLACE « Les index de format du pilote ne se comparent pas entre postes » : le
constat reste vrai, mais le correctif est fait.

Le pilote DNP construit sa liste de formats à l'installation et les numérote à la suite. Le
même papier n'a donc pas le même index d'un poste à l'autre — relevé le 12/08/2026, trois
postes, même pilote, **même liste dans le même ordre** :

| Poste | `(6x4)` | plage valide |
|---|---|---|
| Maisons-Alfort | **121** | 119–129 |
| Créteil | **127** | 125–135 |
| DESKTOP-KT88VDM | **147** | 145–155 |

Le catalogue livré embarque le DEVMODE capturé à Maisons-Alfort. Les deux autres postes
recevaient donc `dmPaperSize = 121`, un index qui **n'existe pas chez eux**, et le pilote
faisait ce qu'il pouvait : la planche identité sortait dans une page trop grande. Chacun a
été rustiné à la main, et le défaut revenait à la publication suivante.

**Le nom, lui, est stable — et le DEVMODE le porte déjà**, dans `dmFormName` : « (6x4) »
dans les trois cas. `DevMode.Apply` appelle donc `RecalerLeFormatPapier` avant toute chose :
on relit le nom, on demande à `PrinterSettings.PaperSizes` comment CE poste le numérote, et
l'on réécrit l'index.

```
DEVMODEW :  dmPaperSize (short) à 78     dmFormName (32 WCHAR) à 102
```

⚠ **Au moindre doute, les octets repartent INCHANGÉS** — pas de nom, format inconnu du pilote
local, DEVMODE tronqué, imprimante muette. C'est le comportement d'avant, qui marchait sur le
poste d'origine et n'a jamais empêché un tirage de partir. On ne remplace une incertitude que
par une certitude.

⚠ **Le tableau reçu n'est jamais modifié sur place** : il vient du catalogue et se partage
d'un tirage à l'autre. Le recalage travaille sur une copie.

Le recalage s'écrit au journal quand il a lieu — c'est la trace qui dira, la prochaine fois,
que le poste n'était pas numéroté comme celui d'origine.

## Les cotes du catalogue sont RELUES à chaque démarrage

`CotesProduit` attrapait la saisie en centimètres — un « 40×50 » réglé sur 40 × 50 mm, qui
sort un timbre. Mais elle ne parlait qu'**à la saisie** : un produit enregistré de travers
avant qu'elle existe, ou saisi sur un poste qui n'avait pas encore la version, ne rencontrait
plus jamais son garde-fou. Le poste `DESKTOP-KT88VDM` en portait deux depuis des semaines.

Deux règles, et il faut les deux :

1. **Le nom rapproché des cotes** — le rapport de DIX exact, qui ne s'invente pas. C'est la
   plus précise : elle dit ce qu'il fallait saisir. Mais elle exige que le nom porte un
   format, et c'est sa limite.
2. **Le filet qui ne demande aucun nom** : `PlusPetitPapierMm = 50`. Le second produit de
   KT88VDM s'appelait « E-PHOTO » et mesurait 10 × 15 mm — rien à rapprocher, donc rien de
   signalé. Un papier ne fait pas cinq centimètres. Le plus petit du catalogue de la boutique
   est le 8×10, qui mesure 80 × 102 : le seuil est bien en dessous, il n'a pas à juger un
   format.

⚠ Les **cases** d'une planche d'identité (35 × 45) ne sont pas concernées : ce sont des
cellules (`SheetCellWidthMm`), pas des produits.

**On ne corrige RIEN.** Le catalogue appartient à l'exploitant ; une correction automatique
sur des cotes changerait ce qui sort du papier sans que personne l'ait demandé. On signale à
deux endroits :

- **au journal, à chaque démarrage** — donc emporté par le rapport de diagnostic. C'est ce
  qui manquait pour voir le défaut à distance, et c'est là qu'il se lit en dix secondes ;
- **dans la LISTE de l'écran Catalogue**, pas dans la fiche du produit : la fiche ne s'ouvre
  que sur celui qu'on soupçonne déjà, or personne ne soupçonnait un produit au nom juste et
  au prix juste. C'est en parcourant la liste qu'on doit buter dessus.

## Le masque du sujet ne dépend PAS de la taille demandée

`MasqueSujet` mémorisait sous une clé qui portait les dimensions de l'image. L'aperçu du
cadrage et la planche rendue à la taille d'impression n'avaient donc pas la même clé, et le
réseau repassait pour rien : **14 495 ms pour une seule photo**, relevés à Créteil le
12/08/2026. C'est aussi ce second passage qui mettait la carte graphique en panne de mémoire
— voir la note sur le repli du détourage.

Or BiRefNet travaille sur une entrée **FIGÉE à 1024 × 1024** et ne remet à l'échelle qu'à la
toute fin (`EnMasque`). Deux tailles de sortie donnent le même masque, à un
redimensionnement près — et redimensionner un masque d'un canal coûte des millisecondes là
où le réseau coûte des secondes.

La clé est donc la PHOTO seule quand l'appelant la nomme. Sans nom, on retombe sur la
signature des pixels, qui distingue d'elle-même deux tailles : le comportement d'avant.

⚠ **La taille reste dans la clé du masque RETOUCHÉ** (`cleRetouchee`) : ce cache-là garde un
masque déjà dilaté, adouci et déjà à l'échelle. Le rendre à une autre taille sortirait un
masque aux mauvaises dimensions.

⚠ **`DejaEnMemoire` ignore désormais les dimensions qu'on lui passe.** Les demander ferait
afficher une barre d'attente au récapitulatif alors qu'il n'y a plus rien à attendre.

## Les masques retouchés se gardent à QUATRE, comme les masques nus

Le masque retouché — contour dilaté, bord adouci — n'avait qu'un seul emplacement, au motif
qu'« on règle une photo à la fois ». C'est vrai d'un curseur, faux d'une planche : l'opérateur
fait l'aller-retour entre les poses avant de choisir, et c'est précisément pour cela que les
masques NUS s'en gardent quatre.

Revenir à la pose précédente jetait donc le seul emplacement, et les curseurs y repayaient
les 360 ms de dilatation et de flou. D'où **« les curseurs sont de nouveau lents PARFOIS »**,
signalé à Créteil le 12/08/2026 : parfois, c'est-à-dire chaque fois qu'on change de photo.

Les deux mémoires suivent maintenant le même va-et-vient, avec la même borne.

## SDK Fuji : toute fonction qui passe une CHAÎNE veut CharSet.Unicode

`PIF_GetOrderInfo` était déclarée sans `CharSet` — donc en ANSI, le défaut de .NET — alors
que ses voisines qui reçoivent le même handle de commande (`PIF_CancelOrder`,
`PIF_ExpressOrder`, `PIF_StartOrder`, `PIF_EndOrder`) sont en Unicode depuis toujours.

Le handle partait en octets simples là où le SDK attend de l'UTF-16 : il ne retrouvait jamais
la commande, `OrderProgress` rendait toujours `null`, et l'avancement retombait sur les
verdicts — qui n'arrivent qu'à la fin, tous ensemble. **La barre restait à zéro d'un bout à
l'autre du tirage, sur les trois boutiques**, le 12/08/2026 : six commandes minilab, six
relevés muets, alors que la veille les sept commandes du même poste avançaient normalement.

Rien ne plantait, rien n'était rouge. `InteropEncodageTests` lit désormais les ATTRIBUTS de
chaque `DllImport` et attrapera la prochaine déclaration ajoutée à la va-vite.

⚠ **Les paramètres `char` sont hors de cette règle**, et l'essai les exclut explicitement :
`PIF_DevIsReady`, `PIF_DevGetPrinterInfo` et leurs voisines reçoivent un identifiant de
machine — « A », « B » — que le SDK attend sur UN octet. Les passer en Unicode en enverrait
deux et casserait la détection des machines, qui fonctionne depuis toujours.

⚠ **Un refus du SDK se dit maintenant au journal**, une fois par handle : c'est ce silence
qui a laissé passer le défaut d'encodage toute une journée. « La machine ne dit rien » ne
disait pas POURQUOI.

## Borner les appels perdus du relais — utile, mais ce n'était PAS la cause du plantage

⚠ **Cette note a d'abord affirmé le contraire, et elle avait tort.** Le relais qui s'arrêtait
en pleine commande à Créteil ne mourait pas de ses fils perdus : voir la note « Le SDK DNP
n'est pas réentrant », qui porte la vraie cause et la preuve. La mesure a démenti le
raisonnement — au moment du diagnostic, le relais tenait en **59 Mo et douze fils**, très loin
des deux gigaoctets. Ce qui suit reste vrai, mais protège d'une panne jamais observée.

Chaque délai dépassé laisse un fil bloqué dans un appel natif que le SDK ne rendra jamais —
c'est écrit depuis toujours dans `RepondreSansJamaisBloquer`, et c'est irréductible : le SDK
ne s'interrompt pas. Rien ne bornait pourtant leur nombre.

Le relais est en **32 bits** : deux gigaoctets d'espace d'adressage, un mégaoctet de pile par
fil. Laisser leur nombre courir n'est pas tenable sur un poste où le SDK se coince
durablement.

`FilsOrphelins` compte les appels partis sans retour. Au-delà de huit, on répond tout de
suite sans rien lancer : le SDK est manifestement coincé, et lui envoyer du travail
supplémentaire ne fait qu'avancer l'heure du crash. Le message le dit et invite à redémarrer.

⚠ **La porte se ROUVRE quand un appel finit par revenir.** Sans ce décompte, un poste
simplement lent resterait fermé jusqu'au redémarrage : on aurait remplacé un plantage par une
panne. C'est le seul point délicat de la classe, et c'est celui que ses essais couvrent le
plus.

⚠ **Huit, et pas un plus grand nombre** : il s'agit d'encaisser une rafale du bandeau pendant
qu'une machine imprime, pas de tolérer un SDK mort. Chaque unité de plus rapproche de la
limite qu'on cherche à ne jamais atteindre.

## Le SDK DNP n'est PAS réentrant — un seul appel à la fois

`cspstat.dll` est une bibliothèque native de 2008 (elle tourne sur `MSVCR90`). Deux chemins
du relais y entrent : `EtatDesDnp`, pour le bandeau, toutes les quelques secondes, et
`TirerSurDnp`, pour un envoi. Comme `RepondreSansJamaisBloquer` donne un fil à chaque
commande, **les deux pouvaient s'y trouver ensemble**.

Le 12/08/2026 à Créteil : **cinq plantages du relais, zéro les six jours précédents**, tous
avec la même signature —

```
Application défaillante : Studio.De100Host.exe
Module défaillant       : MSVCR90.dll
Exception code          : 0xc0000417   (paramètre invalide passé au CRT)
Fault offset            : 0x0003523b   (le même à chaque fois)
```

Le premier arrive dix secondes après un envoi direct DNP. Un `0xc0000417` ne se rattrape
pas : le CRT termine le processus sur-le-champ, sans exception observable côté managé — d'où
un relais qui « s'arrête » sans rien écrire, en pleine commande, et des verdicts qui
n'arrivent jamais.

⚠ **Ce n'est PAS une fuite de mémoire ni une saturation de fils**, contrairement à ce que
laissait croire le commentaire sur les fils orphelins. Mesuré au moment du diagnostic : le
relais tenait en **59 Mo, 281 Mo de virtuel, douze fils** — très loin des deux gigaoctets d'un
processus 32 bits. La piste était séduisante et fausse ; c'est le journal d'événements
Windows qui a tranché, pas le raisonnement.

⚠ **La fenêtre a été ouverte par le correctif de la 1.4.1** : `dnp-print` y est passé de dix
secondes à trois minutes de budget. Le SDK reste donc occupé bien plus longtemps pendant que
le bandeau continue de l'interroger. Le correctif reste juste — c'était la CONCURRENCE qu'il
fallait borner, pas le délai.

**La règle** : tout accès natif à la DNP passe par `verrouDnp`, découverte comprise — c'est
`ListPorts` qui construit la table de ports du SDK, et la reconstruire au milieu d'un envoi
est précisément ce qui tuait le processus.

Le bandeau, lui, ne fait pas la queue : `Monitor.TryEnter` sans attente, et il rend une liste
vide si un tirage occupe le SDK. C'est déjà ce que fait une machine endormie — l'application
complète d'après le spouleur Windows, et rien ne ment à l'écran.

## Monter les agrandissements sur une feuille : le RENDU change, jamais le prix

Un agrandissement rendait un fichier par tirage. Deux 24×30 donnaient deux feuilles de 40×60,
dont la moitié partait à la chute — alors que les deux tiennent sur une seule. Demandé par
l'exploitant le 12/08/2026, pour le circuit **grand format uniquement** : le minilab plafonne
à 210 mm de large, et la DNP a son propre cas (15×20 → deux 10×15) depuis la 1.3.15.

### Le prix ne bouge pas, et c'est ce qui distingue le montage de la planche personnalisée

Les deux mécaniques partagent la géométrie. Elles ne partagent PAS la politique de prix :

| | Facturé sur | Décidé par |
|---|---|---|
| Planche « personnalisée » (impression rapide) | **le papier** — `CustomSheetLayout.Choose` départage au prix | le logiciel |
| **Montage** (agrandissements) | **les tirages** — `UnitPrice × TotalPrints`, inchangé | l'opérateur |

D'où deux champs distincts sur la ligne de commande : `CustomCellWidthMm` bascule `Total` sur
le papier, `MontageSheetCode` ne touche à rien. ⚠ Les confondre ferait payer une feuille
40×60 là où le client doit deux 24×30. C'est pourquoi le montage n'appelle **jamais**
`Choose` — seulement `CapacityDetaillee` et `Distribute`.

### C'est la FEUILLE qui se couche, pas la case

Le calcul à la main disait qu'il fallait coucher la case : deux 24×30 debout font 600 mm de
haut, et l'écart de 2 mm déborde d'un 40×60. Il oubliait que `Capacity` essaie aussi les
**deux sens de la planche**. Feuille couchée, ses 600 mm de large accueillent deux tirages
DEBOUT côte à côte : le cadrage portrait est gardé tel quel, et le fichier sort en 60 × 40.

Conséquence pratique : `PlanMontage.LargeurMm`/`HauteurMm` donnent le sens de composition, et
le rendu doit les employer — composer sur les cotes du catalogue donne une feuille où rien ne
tient. Le pilote oriente ensuite la page ; il sait le faire.

### La photo est rendue à SON sens, puis tournée à la pose

Le circuit des agrandissements oriente sa toile par photo (`CropMath.OrientCanvas`) : un
portrait sort en 24×30, un paysage en 30×24. Une empreinte, elle, est unique pour toute la
grille.

⚠ **On ne rend donc PAS la photo au format de l'empreinte** — ce serait la recadrer dans le
mauvais sens, sur du papier cher, sans que rien ne le signale avant le client. Chaque photo
est rendue à son orientation, et c'est l'IMAGE RENDUE qu'on tourne d'un quart de tour pour la
poser (`RenderCustomSheetToFile`, paramètre `footprint`). Un quart de tour est exact, aucun
pixel n'est interpolé, et le tirage découpé retrouve son sens dans la main de l'opérateur —
c'est déjà ce que fait la planche identité composée debout.

Bénéfice : une sélection **mêlant portraits et paysages** se monte sur la même feuille.

### Rien ne change tant qu'il n'y a rien à gagner

`MontageFeuille.MinimumUtile = 2` est la garde principale, et elle vit dans la géométrie
plutôt que répétée chez chaque appelant :

- une feuille qui ne porte qu'un tirage ne donne **aucun plan**, donc aucun candidat, donc
  l'écran de choix ne s'affiche pas et le parcours est celui d'avant ;
- un montage à une case par feuille n'est pas un montage, c'est un tirage avec des traits de
  coupe en plus — une régression silencieuse pour tous les postes qui ne demandent rien.

Au rendu, `PlanDeMontage` **se replie en silence** sur un fichier par tirage si la feuille a
disparu du catalogue, a été désactivée, est passée sur une autre machine, ou est devenue trop
petite. Le choix a pu être fait des heures avant le tirage, parfois avant une modification du
catalogue : une commande déjà encaissée ne doit pas se refuser à sortir pour cette raison. Le
journal dit ce qui s'est passé.

### Le choix suit le format, et survit à une mise de côté

Il est fait à un écran (`MontageFeuilleView`) intercalé entre la tuile de format et le choix
des photos, **famille agrandissement seulement**, avec « une par feuille » en tête de liste —
un opérateur qui découpe lui-même ne veut pas d'un montage, et ce geste doit rester à un clic.

Le choix vaut pour CE format : une photo passée à un autre produit repart en tirage
ordinaire, ce qui garde aussi la grille pleine — une ligne montée ne mélange qu'un format. Et
il voyage dans `TravailEnAttente` : perdu à la reprise, il ferait ressortir la commande sur
deux fois plus de papier sans que personne s'en aperçoive.

## L'historique des 30 jours : un journal à part, et pourquoi pas les commandes

Studio Photo Identité garde les photos **faites** — imprimées ou envoyées par courriel —
trente jours, avec le réglage exact de chacune, pour les rouvrir sans rien remettre.
Une photo simplement OUVERTE n'y entre pas : la carte d'un client en porte quatre-vingts.

Le journal vit dans `identite\historique\` (`HistoriqueIdentite`), un fichier par photo, et
non dans les commandes. **Deux raisons dures :**

- les commandes **ne gardent pas les repères** de crâne et de menton (`TraduireIdentite` le
  documente). Une planche rouverte sans eux relance la détection de visage et écrase le
  placement manuel — c'est-à-dire précisément ce qu'on vient chercher ;
- une photo **envoyée par courriel** n'est pas reconnaissable comme une identité dans une
  commande : elle porte le produit « envoi courriel », sans taille de case.

Écrire les repères dans `OrderItem` reviendrait à changer l'enregistrement **comptable** pour
un confort d'écran. Les commandes restent la vérité comptable ; le journal n'est qu'un index
de travail — il ne facture rien et ne solde rien.

Ce qu'il porte est un `TravailEnAttente`, le même objet que les planches mises de côté : une
seule forme à faire évoluer, et `IdPhotoView(TravailEnAttente)` sait déjà la rouvrir.
**Une tuile touchée rouvre la photo SUR L'ÉCRAN DE TRAVAIL**, avec un `Id` neuf — donc avec
« Imprimer » et « Envoyer par e-mail », qui sont ceux de l'écran. Pas d'écran séparé aux
fonctions limitées.

La clé d'une entrée est **le fichier ET la journée** : imprimer puis envoyer la même photo
fait une tuile et deux pastilles, pas deux tuiles. Le nom du fichier du journal en est déduit
(SHA-256 tronqué) : retrouver une entrée est une lecture, jamais un parcours de dossier.

⚠ Le bouton « 🕘 Photos récentes · 30 j » n'est visible que si `Mode.IsIdentite` — le
LOGICIEL, pas la session (`EnIdentiteVerrouille`) : sortir par le PIN pour dépanner ne doit
pas faire disparaître l'historique sous les doigts de l'opérateur. Le journal, lui, est écrit
par les deux applications, qui partagent la racine de données.

### ⚠ Une photo a DEUX noms, et la reprise a besoin des deux

Le nom du **client** (`IMG_1234.jpg`) est celui que l'opérateur reconnaît : il part dans la
commande, dans les messages, et sur la tuile de l'historique. Le nom **sur le disque** est
celui du fichier qu'on lit vraiment — et dès qu'on passe par l'historique, c'est celui de la
copie de travail (`IMG_1234-ab12cd34.jpg`), puisque la carte du client est repartie avec lui.

La bande de gauche se remplit depuis les CHEMINS : elle porte donc le nom sur le disque, alors
que la fiche a gardé celui du client. `AppliquerLAttente` cherchait le second et ne trouvait
rien : **la boucle sautait toutes les photos et la planche revenait vide de tout réglage** —
cadrage, repères, fond blanc, corrections à neutre, sans erreur à l'écran ni une ligne au
journal. Une planche mise de côté, elle, est reprise sur le support du client, où les deux
noms se confondent : le défaut ne touchait QUE l'historique, c'est-à-dire tout ce que cette
passe existe pour rendre.

`PhotoIdentiteEnAttente` porte donc les deux (`FileName` et `NomSurLeDisque`), et
`RepriseDeLaPlanche` — sorti du code-behind pour être essayable, comme `CopieDeTravail` —
indexe par l'un et par l'autre, **le nom du client d'abord** : deux clients apportent souvent
un `IMG_1234.jpg`, et reprendre le cadrage d'une inconnue sortirait le visage de quelqu'un
d'autre. La photo retrouvée est ensuite **renommée** au nom du client, pour que la commande
suivante et la tuile suivante ne portent pas le nom d'une copie.

## ⚠ Une mémoire vidéo MESURÉE ne se compare pas à un chiffre commercial

Le modèle de détourage « portrait » demande de la mémoire vidéo, et le seuil a été relevé
deux fois — dans les deux sens, pour la même raison de fond.

- **12/08/2026** : seuil à 6, comparaison `>= 6`. La GTX 1660 SUPER de Créteil annonce
  6,0 Go tout ronds : elle passait, et elle a échoué en boutique. Seuil monté à 8.
- **20/08/2026** : la RTX 5060 du Kremlin-Bicêtre annonce **7,96 Go** pour une carte de
  8 Go. Comparée à 8, elle échoue — et avec elle **toute carte grand public de 8 Go**,
  puisqu'elles réservent toutes un peu de mémoire avant de la déclarer. Le modèle puissant
  était devenu inatteignable en pratique, sans que rien ne le dise.

D'où `MargeDeMesureGo = 0,5` : le plancher comparé au relevé est `8 − 0,5`, quand `8` reste
ce qu'on annonce à l'exploitant. Un demi-gigaoctet rattrape l'écart de déclaration sans
laisser entrer la classe du dessous — **il ne se vend rien entre 6 et 8 Go**.

⚠ Et la règle vit dans **`DetourageSettings.AssezDeMemoirePourLeModelePuissant`**, une seule
fois. Elle était recopiée à TROIS endroits — le choix du modèle au démarrage
(`AppServices`), l'avertissement des réglages et le grisage du bouton
(`ReglagesDetourageView`). Trois copies d'un même seuil, c'est ainsi qu'un seuil diverge.

⚠ Le chiffre lui-même ne vient pas de WMI : `Win32_VideoController.AdapterRAM` est un
`uint32` et plafonne à 4 Go. Le relevé juste est au registre, en
`HardwareInformation.qwMemorySize`.

## Les masques de détourage SURVIVENT au lancement

`MasqueSujet` garde quatre masques en mémoire, et ils meurent avec l'application. Ils sont
désormais écrits sous `cache\masques\<méthode>\<empreinte>.png` : lecture mémoire → **disque**
→ réseau. Une photo rouverte le lendemain retrouve son fond blanc en quelques millisecondes
au lieu de repayer plusieurs secondes de réseau — et surtout, elle retrouve **le même** : sur
une carte à 4 Go, c'est le second passage qui manque de mémoire et rend un fond dégradé.

⚠ **Un sous-dossier par méthode** (`couleur`, `birefnet-lite-fp16`, `birefnet-portrait-fp16`).
Sans lui, changer de modèle dans les réglages ne changerait plus rien à ce qui sort, et le
réglage passerait pour inopérant — défaut invisible et pénible à retrouver.

⚠ Les octets ne viennent plus forcément de nous : signature PNG vérifiée à la lecture, fichier
abîmé effacé, décodage protégé. Un masque illisible fait **renoncer à la correction**, jamais
éclater un rendu — c'est déjà la règle de cette classe.

`DejaEnMemoire` regarde le disque aussi, sinon l'écran annonce six secondes d'attente pour
aller lire un fichier en cinq millisecondes.

## Le cache se purge à 30 jours, et c'est la MÊME rétention que l'historique

`cache\travail` reçoit une copie de chaque photo ouverte au comptoir — c'est ce qui la sauve
du retrait de la carte du client. **Rien ne le purgeait**, sur quatre postes.
`MenageDuCache` efface les journées de plus de trente jours (la date vient du **nom** du
dossier, comme l'archivage des commandes, et non d'une date d'écriture qu'une sauvegarde
rafraîchit) et les masques de plus de trente jours.

La rétention est celle de l'historique, et ce n'est pas une coïncidence : **la fiche et les
pixels qu'elle désigne doivent disparaître ensemble.** C'est ce qui impose la règle la moins
évidente du lot — une copie de travail d'un AUTRE jour est recopiée dans le dossier du jour
(`MettreALAbriAsync`). Sans cela, une photo rouverte le vingtième jour et réimprimée laisse
une fiche vivante jusqu'au cinquantième, pointant sur un fichier effacé au trentième.

Le ménage tourne dans les DEUX applications (`AppServices.MenageDuCacheEnFond`) : c'est le
poste identité qui ouvre des cartes toute la journée. L'entretien complet — archivage des
commandes, sauvegarde — reste au Studio de la boutique.
