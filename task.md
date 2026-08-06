# Exécution — 06/08/2026, après-midi (DS620 et cadrage)

## I. Le décalage des couleurs de la DS620 — ce qui est ÉTABLI ⏳

L'exploitant gâche du papier sur les planches identité. Le défaut n'apparaît jamais avec
DiLand, sur aucun poste.

- [x] I1 **Les fichiers qu'on envoie sont PROPRES.** Les rendus des commandes 06-005 et
      06-006 ont été ouverts et regardés : aucun fantôme, aucune dominante. Le défaut naît
      APRÈS nous — ce n'est ni le pipeline, ni le détourage, ni l'ICC
- [x] I2 **DiLand n'utilise pas le pilote Windows** pour cette machine. Son journal montre
      le SDK en direct : `GetFreeBuffer` → `WaitForFreeBuffer` → `SetMediaSize` →
      `SetOvercoatFinish(GLOSSY)` → `SendImageData`
- [x] I3 ⛔ **L'envoi direct est hors de portée** : sonde 32 bits lancée DiLand ouvert —
      `Ports trouvés : 0`, `0x80000000` sur les trois rangs. DiLand tient le port USB en
      exclusif, et il tourne en permanence. Décision de l'exploitant : on garde DiLand
      ouvert, donc on reste sur le pilote
- [x] I4 Deux tirages du MÊME fichier à une minute d'écart ont donné des défauts
      DIFFÉRENTS. Un réglage figé donnerait toujours le même : ce qui varie tient au
      tampon, à la mécanique, ou aux deux

## J. Ce que le pilote cachait ✅

- [x] J1 `LectureDevMode` : les réglages d'un DEVMODE, dits en français. Mille deux cents
      octets opaques dont personne — ni l'opérateur, ni celui qui dépanne à distance — ne
      pouvait dire le contenu
- [x] J2 ⚠ On CHERCHE les noms connus au lieu de compter les chaînes deux par deux : le
      bloc réel s'ouvre par les marqueurs d'Unidrv, et le découpage par paires annonçait
      « OPTYPE_LUSTER = PRINTBUFFCONTROL ». Un essai le tient
- [x] J3 Deux réglages signalés comme dangereux, et ce sont les deux suspects :
      `Resolution = Option1` (**mode rapide**) et `PRINTBUFFCONTROL = PBC_NONCLEAR`
      (**tampon non vidé entre deux tirages**)
- [x] J4 Les réglages partent au JOURNAL à chaque enveloppe, et s'affichent à la capture
- [x] J5 `Studio.PrintProbe devmode-lire <fichier>` les dit en ligne de commande
- [x] J6 Ce qui part au pilote est aplati en **24 bits sur du blanc** : nos rendus sont des
      PNG 32 bits, et la DS620 annonce `ColorMode=24bpp`. GDI+ convertissait à la volée,
      dans le chemin d'impression, à chaque tirage

## K. Cadrage automatique sur le visage ✅

- [x] K1 Réglage du poste, **décoché par défaut** : le cadrage automatique déplace le cadre
      de photos que l'opérateur n'a pas ouvertes
- [x] K2 Le regard aux **deux cinquièmes** de la hauteur, pas au milieu : un portrait dont
      les yeux tombent au centre paraît tassé
- [x] K3 Il ne fait que DÉPLACER : ni format, ni zoom. Neuf essais le tiennent, dont celui
      qui vérifie qu'aucun bord blanc n'apparaît jamais
- [x] K4 Ne touche ni un cadrage venu d'une borne, ni la « photo entière », ni rien dès que
      l'opérateur a posé un geste. La détection tourne en tâche de fond, une photo à la fois

## L. Le raccourci du bureau ✅

- [x] L1 Il lance un `.cmd`, et Windows lui donnait donc l'icône de l'invite de commandes
- [x] L2 `tools\Creer-Raccourci.ps1` pose l'icône de l'application, sur n'importe quel poste

- [x] J7 ⚠ **Deux corrections après la copie d'écran du dialogue** (06/08/2026) : le pilote
      propose bien le **Brillant**, contrairement à ce que ses noms internes laissaient
      croire — `OPTYPE_LUSTER` s'affiche « Brillant ». Et `PRINTBUFFCONTROL` s'appelle
      **« Réessayer l'impression »** dans le dialogue : chercher « tampon » ne donne rien
- [x] J8 Les **fonctionnalités d'impression avancées** de la file (actives sur ce poste)
      partent aussi au journal : le spouleur rejoue alors le rendu dans son processus

## Reste à faire par l'exploitant (06/08/2026, après-midi)

Dans le dialogue du pilote DP-DS620 (Catalogue → planche identité → capturer les réglages),
**une modification à la fois**, avec une planche tirée entre chaque — sinon on ne saura pas
laquelle a agi :

1. Caractéristiques de l'imprimante → **« Réessayer l'impression » = Désactiver**
   (c'est `PBC_NONCLEAR`, le suspect n°1 : l'image reste en mémoire d'un tirage à l'autre) ;
2. Graphique → **« Qualité d'impression » = High-quality** (au lieu de High-speed) ;
3. Propriétés de l'imprimante → onglet Avancé → décocher **« Fonctionnalités d'impression
   avancées »** (celui-là ne se capture pas, il appartient au poste).

Si le fantôme survit aux trois : imprimer le MÊME fichier depuis DiLand. S'il sort propre,
le pilote est en cause ; s'il sort fantômé, c'est l'entraînement de la machine, et aucun
réglage ne la réparera.

# Exécution — passe du 06/08/2026 (retours du comptoir)

Dix retours d'un même après-midi d'exploitation. Sept touchent l'écran « Modifier », deux le
papier, un l'identité de l'application.

## A. Le bouton « Dupliquer » ne faisait apparaître aucune vignette ✅

- [x] A1 Le doublon naissait **sans vignette source** : `RefreshThumbnail` sortait
      immédiatement et la case restait vide, dans la planche comme dans la bande
- [x] A2 Il naissait aussi **sans la définition du fichier**, donc `PhotoItem.Cadre` rendait
      `null` — et le doublon partait au tirage en **pleine image**, sans le cadrage de son
      original. C'est le défaut grave des deux : il ne se voit qu'une fois le papier sorti
- [x] A3 Vérifié côté impression : `OrderService.CreateOrder` copie le fichier une seule
      fois mais crée bien **deux `OrderItem`**. Le doublon part
- [x] A4 `PhotoItem.Cle` : les caches d'aperçu étaient rangés par chemin, que l'original et
      son doublon partagent. Passer l'un en noir et blanc donnait son image à l'autre

## B. La case « Contour de découpe » ne se cochait pas ✅

- [x] B1 Elle était **grisée** en « remplir le format » — le mode par défaut de presque tous
      les produits. Le clic ne pouvait pas la cocher
- [x] B2 Et pour cause : `ImagePipeline` ne traçait rien dans ce mode. Le bord de la photo
      EST le bord du tirage, et c'est là que passent les ciseaux quand plusieurs tirages
      sortent sur la même feuille. Le trait s'y pose désormais
- [x] B3 La case montrait l'état de la photo AFFICHÉE alors que le clic porte sur les photos
      VISÉES : elle se remettait à zéro juste après avoir été cochée

## C. Le format Polaroid ne ressemblait pas à un Polaroid ✅

- [x] C1 Sur photo du tirage : rien ne marquait le bord du cadre, qui ne remplit pas la
      feuille — le résultat lisait comme une photo posée au milieu du blanc
- [x] C2 Le contour est désormais tracé **d'office** sur ce format, sans passer par la case
- [x] C3 Plus des **repères d'angle** dans le blanc autour, à 1 mm : ils partent avec la
      chute, là où le contour reste sur le tirage. Omis là où il n'y a pas la place

## D. La planche identité : date rognée, heure absente ✅

- [x] D1 La date était à 1,2 mm du bord ; le fond perdu en mange près de 1,5 sur chaque
      côté. **4 mm** de marge latérale, et elle est hors d'atteinte du rognage
- [x] D2 L'heure est écrite à la suite, en corps réduit (0,72) et en gris : c'est la DATE
      qui prouve qu'une photo est récente, l'heure n'est qu'une précision d'atelier
- [x] D3 La mention en gras et capitales se majorait à 0,58 cadratin comme le reste : elle
      mordait sur le code QR. 0,68 pour la première ligne

## E. Faire de la place à ce qu'on est venu regarder ✅

- [x] E1 **Cadrage identité** : trois boutons héritaient des 76 px de `BigButton` et
      imposaient leur hauteur à toute la barre. Titres, air et boutons resserrés — la barre
      perd près d'un tiers de sa hauteur, qui revient à la photo
- [x] E2 **Récapitulatif de planche** : deux cartes empilées, quatre rangées de boutons,
      près de trois cents pixels sous une planche qu'on est venu JUGER. Tout tient
      maintenant sur une seule barre qui se replie

## F. Les gestes de la sélection ✅

- [x] F1 **Maj+clic** manquait à la bande de « Modifier » : viser vingt photos qui se
      suivent demandait vingt Ctrl+clic. Elle VISE, elle ne bascule pas — comme sur la
      planche, et pour la même raison
- [x] F2 Sur la planche, le Maj+clic ne faisait rien tant qu'aucun clic simple n'avait posé
      d'ancre : après un Ctrl+A ou un « tout », le geste passait pour absent. L'ancre
      retombe sur la dernière photo touchée
- [x] F3 Ctrl+A basculait déjà des deux côtés ; il le DIT maintenant dans le journal, seul
      moyen de vérifier après coup ce qu'un raccourci a fait

## G. Ce qui n'est pas cadré n'est plus assombri ✅

- [x] G1 Le voile noircissait précisément la partie qu'on regarde pour décider de la
      rattraper. Retiré des quatre endroits : surface de recadrage, éditeur, cadrage
      identité, et vignettes de la planche
- [x] G2 Les vignettes de la bande de « Modifier » se touchaient : de l'air tout autour, et
      le fond d'une photo visée ne déborde plus sur sa voisine

## H. L'application a un logo ✅

- [x] H1 Un diaphragme orange dans un anneau bleu, aux couleurs de l'application
- [x] H2 Six définitions de 256 à 16 px, chacune tracée quatre fois trop grand puis réduite
- [x] H3 `tools\Studio.Logo` le redessine ; l'icône est **versionnée**, pas fabriquée à la
      compilation

## Vérification

- [x] `dotnet build` de la solution : 0 erreur
- [x] `dotnet test` : **1216 essais, 0 échec** (deux ajoutés sur le contour en « remplir »,
      un sur les traits de coupe du Polaroid ; un ancien essai retourné, qui vérifiait que
      la case ne posait justement rien)
- [x] Tirages d'essai rendus et regardés : Polaroid et planche identité horodatée
- [x] L'application démarre, la fenêtre porte son icône

## Reste à faire par l'exploitant (passe du 06/08/2026)

- Sortir **un** Polaroid sur la DS620 et vérifier que le trait de coupe tombe où il faut
- Sortir **une** planche identité et vérifier que la date n'est plus rognée à gauche
- Dire si le voile doit revenir sur le cadrage identité : c'est le seul écran où il pouvait
  se défendre, et il a été retiré par cohérence avec les trois autres

# Exécution — 14ᵉ passe (vitesse des rendus)

## W. Le décodage lisait 24 Mpx pour en garder 0,2 ✅

**Mesuré d'abord, optimisé ensuite.** Les journaux disaient qu'une planche d'identité met
plusieurs secondes ; ils ne disaient pas dans quoi. La sonde `Studio.RenduProbe` le dit, sur
la vraie photo de la commande 05-026 (6016 × 4000 pour une cellule de 413 × 531) :

| Étape | Avant |
| --- | --- |
| décodage | 320 ms |
| AutoOrient | 211 ms |
| **réduction avant redressement** | **920 ms** |
| **redressement 1,25°** | **1024 ms** |
| recadrage + mise à l'échelle | 48 ms |
| écriture PNG | 155 ms |

Le décodeur JPEG sait rendre directement au demi, au quart ou au huitième — c'est du
sous-échantillonnage exact, pas une réduction après coup. `ThumbnailService` s'en servait
déjà pour les vignettes ; le rendu, non.

- [x] W1 `LectureEconome` : le JPEG est décodé à la taille dont le TIRAGE a besoin. La
      réduction qui suit n'a plus que six mégapixels à rééchantillonner au lieu de vingt-quatre
- [x] W2 **On demande un CARRÉ, du plus grand des deux côtés** : l'indication porte sur le
      fichier, dont l'orientation n'est connue qu'après lecture de l'EXIF. Demander
      1194 × 1796 sur un fichier couché ferait décoder trop petit, et le tirage y perdrait
- [x] W3 **Le sur-échantillonnage ne se justifie QUE devant un redressement.** Sans lui, la
      source va directement à sa taille finale par un seul rééchantillonnage — c'est le cas
      le plus fréquent de la boutique, et il gardait deux fois trop de pixels pour rien
- [x] W4 Jamais à la hausse : le décodeur ne sait pas agrandir, et un agrandissement doit
      lire la source entière

**Résultat, sur la photo réelle :**

| Rendu | Avant | Après |
| --- | --- | --- |
| planche d'identité (redressée) | 2587 ms | **1696 ms** (−34 %) |
| 10×15 ordinaire | 1216 ms | **815 ms** (−33 %) |

Sur une commande de trente 10×15, cela fait passer le rendu de 36 s à 24 s.

## X. La qualité, vérifiée et FIGÉE ✅

Un gain de temps qui abîmerait les tirages ne serait pas un gain.

- [x] X1 Écart RMS mesuré sur la vraie photo : **0,00209** pour la cellule d'identité,
      **0,00302** pour le 10×15. La 13ᵉ passe avait déjà accepté 0,0096 pour la réduction
      avant redressement — on est cinq fois en dessous
- [x] X2 essais : `RenduEconomeTests` (6), qui comparent le rendu économe au rendu pleine
      résolution et refusent au-delà de 0,02
- [x] X3 ⚠ **Les essais mesurent sur des formes PLEINES, pas sur une mire de traits fins.**
      Une mire d'un ou deux pixels est un cas d'aliasing qu'aucune photo ne présente : elle
      refusait une réduction que la vraie photo traverse à 0,002. On mesure ce qu'on imprime

## Y. Ce qui a été mesuré et LAISSÉ tel quel

- [x] Y1 **Le redressement (1017 ms sur 2 Mpx) est intrinsèque.** Quatre variantes essayées
      — alpha désactivé, sans virtual pixels, `Distort ScaleRotateTranslate` — toutes à
      1010-1020 ms. Ce Magick.NET est bâti sans OpenMP : seul le nombre de pixels compte,
      et il est déjà au minimum
- [x] Y2 **Le fil de l'interface ne bloque nulle part** : aucun `.Result`, `.Wait()`,
      `GetAwaiter().GetResult()` ni `Thread.Sleep` dans `Studio.App`. La lenteur ressentie
      venait des rendus, qui tournent déjà en tâche de fond
- [x] Y3 Le sur-échantillonnage de 2 devant un redressement n'est PAS abaissé : la qualité
      prime sur 300 ms, et c'est un tirage qu'on vend

## Vérification

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1139 verts**

---

# Exécution — 14ᵉ passe (revue avant livraison)

Relecture complète des 2 260 lignes changées, avant de livrer. Cinq défauts trouvés — dont
un qui touchait les commandes de clients.

## R. La sélection écrasait la QUANTITÉ du client ✅

Le format avait été corrigé (H), pas la quantité. `SelectAll` et la sélection par plage
posaient `photo.Quantity = _quantity` **sans condition** : une photo commandée en trois
exemplaires à la borne repassait à un seul dès qu'on cochait « tout ».

`Toggle` (le clic simple), lui, ne touchait à rien quand la photo avait déjà un produit —
d'où un logiciel qui se comportait différemment selon qu'on cochait une photo ou toutes.

- [x] R1 `Prendre()` : un seul point d'entrée pour faire entrer une photo dans la commande.
      Le format et la quantité du bandeau ne s'appliquent qu'aux photos qui n'ont RIEN
- [x] R2 Les trois chemins — clic, plage, tout sélectionner — passent par lui
- [x] R3 ⚠ Non couvert par un essai : `Studio.App` n'est pas référencé par les tests, comme
      l'ordre des affectations du cadrage de borne

## S. Le débord négatif tronquait la photo ✅

`RemplirLeDebord` n'appliquait le facteur d'échelle que s'il était supérieur à 1. Une
machine réclamant MOINS que les cotes nues aurait vu sa photo rognée au centre au lieu
d'être réduite — sortie amputée de ses bords, sans que rien ne le dise.

- [x] S1 Le facteur s'applique dans les deux sens
- [x] S2 essai : `Une_cible_plus_petite_fait_reduire_la_photo_et_non_la_tronquer`
- [x] S3 Le cas ne se présente pas sur les machines de la boutique (débord toujours
      positif, +35 px) — mais il serait sorti sans bruit

## T. Les relevés du compteur pouvaient se chevaucher ✅

Le battement de 10 s ne suspendait pas ses coups pendant l'attente de la machine : un
relais lent — ou occupé à imprimer — laissait les lectures s'empiler.

⚠ **C'est exactement ce qui tuait le relais 32 bits** (voir G) : on ne pouvait pas le lui
redemander. Un verrou simple garantit un seul relevé à la fois.

## U. Les doublons revenaient dans l'ordre inverse ✅

`RecreerLesDoublonsEnAttente` insérait chaque doublon juste après l'originale : sur une
photo tirée en 10×15, 13×18 puis 15×20, les deux derniers formats se croisaient à la
reprise. La position d'insertion avance désormais.

## V. Chargements de vignettes empilés ✅

Changer d'onglet dans « Commandes du jour » relançait une lecture par onglet visité —
quatre-vingt-treize commandes à quatre vignettes — sans arrêter les précédentes. Le
chargement précédent est maintenant abandonné.

## Ce que la revue a CONFIRMÉ comme sain

- les options JSON du protocole ne sont mutées nulle part ailleurs : `MakeReadOnly` est sûr ;
- `SelectionnerLaPlage` supporte l'ancre disparue et l'ordre inversé ;
- `OnDupliquer` recalcule le rang à chaque tour : les insertions successives ne se décalent pas ;
- les corrections du doublon sont bien une COPIE (`Clone`) et non l'instance partagée ;
- `Choisir` d'une imprimante et la liste de l'écran puisent à la même source : la
  présélection ne peut pas désigner un absent.

## Le tirage identité aux couleurs fausses : ce n'est PAS le logiciel

Planche sortie en franges vert/magenta le 05/08/2026 à 17:53, correcte à la relance.

**Le fichier rendu par Studio pour la planche RATÉE est parfaitement correct** — vérifié en
l'ouvrant. Les deux commandes (05-026 et 05-027) ont produit le même rendu, aux mêmes
cotes, par le même code.

Les franges de couleurs complémentaires décalées sont la signature d'un décalage entre les
passes de la sublimation : la DS620 dépose le jaune, le magenta et le cyan en trois
passages, et le papier a patiné entre deux d'entre eux. C'est mécanique.

## Vérification

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1133 verts**

---

# Exécution — 14ᵉ passe (donner l'application aux collègues)

## P. Signaler un problème depuis le poste ✅

Les journaux ont permis de trouver le liseré blanc et le « Pipe is broken » — mais ils
étaient sur le poste de la boutique, sous la main. Sur celui d'un collègue, personne ne va
lire `D:\PhotoStudioData\logs`.

- [x] P1 `RapportDiagnostic` : une archive avec les 7 derniers jours de journaux, les
      réglages, le poste, la VERSION et le mot de l'opérateur
- [x] P2 **Rien ne part tout seul** : c'est un bouton, dans les paramètres. Le poste
      travaille sur les photos de clients, et l'on n'expédie rien sans qu'on l'ait demandé
- [x] P3 **Aucun secret ne part.** `mail.json` porte le mot de passe de la boîte du magasin
      et `dropbox.json` le jeton : ils sont écartés, ainsi que la clé WiFi. En cas de doute
      sur un nom de fichier, on écarte
- [x] P4 Aucune photo n'est jointe : les journaux NOMMENT des fichiers, ils n'en
      transportent aucun
- [x] P5 Les journaux sont tronqués à 2 Mo, en gardant la FIN : c'est là qu'est ce qui vient
      de se passer, et un serveur refuse au-delà de 25 Mo
- [x] P6 Le journal du JOUR est ouvert par l'application elle-même : ouvert en partage, sans
      quoi le rapport échouerait sur le seul fichier qui compte
- [x] P7 Un second bouton écrit le fichier SANS courriel — un poste dont l'envoi n'est pas
      configuré est justement celui dont on a le plus besoin des journaux
- [x] P8 Vérifié SUR LES VRAIES DONNÉES (`tools\Studio.RapportProbe`) : 6 jours repris,
      115 Ko, et le mot de passe réel du poste ne se retrouve nulle part dans l'archive
- [x] P9 essais : `RapportDiagnosticTests` (16)

## Q. Mise à jour automatique et distribution ✅

Le raccourci du bureau recompile depuis les sources : c'est le poste de celui qui
développe. Un collègue n'a ni le SDK, ni le dépôt.

- [x] Q1 `MiseAJour` : la dernière publication du dépôt, lue par l'API GitHub
- [x] Q2 **On vérifie, on annonce, l'opérateur décide.** Une mise à jour qui s'installerait
      seule fermerait l'application — peut-être au milieu d'une commande, devant un client
- [x] Q3 Brouillons et préversions écartés : les envoyer ferait tirer sur du code qu'on n'a
      pas fini d'écrire
- [x] Q4 Une publication SANS archive n'est pas proposée : mieux vaut le dire que d'offrir
      une mise à jour qui échouerait au téléchargement
- [x] Q5 **Strictement plus récente** : republier la même version — pour corriger sa
      description — ne doit pas proposer une réinstallation à tous les postes
- [x] Q6 Ne lève JAMAIS pour une raison de réseau : hors ligne, quota dépassé, dépôt
      injoignable sont des circonstances ordinaires, et aucune n'empêche de travailler
- [x] Q7 **L'installation passe par un script**, parce que Windows verrouille les fichiers
      d'un programme qui tourne : il attend la fermeture, recopie, relance. Il arrête aussi
      le relais du minilab, qui tient les mêmes DLL
- [x] Q8 `tools\Publier.ps1` : version autonome (aucun runtime à installer chez le
      collègue), archive, publication GitHub. Il REFUSE de publier une version déjà parue —
      sinon aucun poste ne verrait rien
- [x] Q9 Le relais 32 bits est publié dans `de100\`, **là où l'application le cherche** :
      ailleurs, elle ne le trouverait qu'en remontant vers une sortie de compilation qui
      n'existe pas chez un collègue
- [x] Q10 **Vérifié en vrai** : l'archive autonome (241 Mo) se fabrique, l'application
      publiée démarre, trouve son relais, voit les commandes des bornes et les trois
      machines du bandeau
- [x] Q11 essais : `MiseAJourTests` (22)

## Ce qu'il reste à faire à la main

1. Monter `<Version>` dans `Directory.Build.props` avant chaque publication ;
2. `gh auth login` une fois sur le poste qui publie ;
3. `.\tools\Publier.ps1 -Notes "ce que ça corrige"`.

## Vérification

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1132 verts**

---

# Exécution — 14ᵉ passe (poste et matériel)

## L. Dupliquer une photo pour la tirer dans deux formats ✅

- [x] L1 Bouton « ⧉ Dupliquer » dans « Modifier », sur les photos cochées
- [x] L2 **C'est la GRILLE qui duplique**, jamais l'écran : elle seule tient la liste que
      l'impression parcourt. Un doublon ajouté à la seule liste de « Modifier » se serait
      affiché, se serait réglé, et ne serait jamais sorti
- [x] L3 Le doublon se range JUSTE APRÈS son original, des deux côtés — sur soixante photos,
      le retrouver en fin de planche demanderait de tout faire défiler
- [x] L4 **Ce sont les doublons qui restent cochés**, et les originaux qui sont relâchés :
      le geste suivant est toujours « et maintenant, en 15×20 »
- [x] L5 Les corrections sont COPIÉES (`Clone`) et non partagées : retoucher le doublon
      aurait sinon retouché l'original
- [x] L6 **La mise en attente supporte les doublons** : deux entrées de même nom de fichier,
      consommées À LA FILE à la reprise. Une simple recherche par nom donnait la première
      aux deux vignettes, et le second format était perdu
- [x] L7 Les doublons sont RECRÉÉS à la réouverture : le dossier ne porte qu'un fichier, le
      balayage ne fabriquait donc qu'une vignette
- [x] L8 essais : `AttenteDoublonsTests` (4)

## M. Les bornes se trouvent toutes seules ✅

Le dossier de DiLand était écrit en dur. Juste sur le poste de la boutique, faux partout
ailleurs — c'était le premier obstacle à donner l'application à un collègue.

- [x] M1 `DiLandLocator` : quatre pistes, de la plus sûre à la plus large — le réglage du
      poste, le PROCESSUS DiLand s'il tourne (aucune supposition sur le dossier ni le
      disque), les deux « Program Files », les racines des disques fixes
- [x] M2 **On ne balaie pas les disques en profondeur** : des minutes au démarrage pour un
      gain nul. Seules les racines, ce qui couvre « installé sur D: »
- [x] M3 Le dossier d'INSTALLATION suffit — personne ne retient
      « Data\AllUsersData\Repositories\Default »
- [x] M4 La base OU les dossiers de commandes suffisent : DiLand purge sa base alors que
      les dossiers restent, et Studio sait encore en tirer les photos
- [x] M5 Un réglage périmé laisse la détection reprendre la main : un chemin devenu faux
      rendrait sinon l'application aveugle
- [x] M6 Vérifié SUR LE POSTE : la sonde retrouve le dépôt réel et lit les commandes du jour
- [x] M7 essais : `DiLandLocatorTests` (10)

## N. Les imprimantes se reconnaissent par FAMILLE ✅

« SC-P800 » était cherché dans le nom. Windows nomme pourtant la machine de la boutique
`EPSONFECE59 (SC-P800 Series)` : cela marchait par chance. Chez un collègue équipé d'une
P700, d'une DS-RX1 ou d'une Citizen, plus rien n'était trouvé — et rien ne le disait.

- [x] N1 `DetectionImprimantes` : trois rôles (agrandissements, sublimation, minilab),
      reconnus sur la MARQUE et la GAMME. « SureColor », « DS », « CP » couvrent des
      générations entières là où « SC-P800 » ne couvre qu'un exemplaire
- [x] N2 **Le photocopieur de bureau n'est pas un traceur** : l'iR-ADV du magasin sort du A3
      sur papier ordinaire, et proposer un agrandissement dessus ferait perdre un tirage.
      On ne retient jamais une marque entière, seulement ses gammes photo
- [x] N3 Les files virtuelles sont écartées d'abord : « Microsoft Print to PDF » contient
      « Print », et « Send to Sawgrass Print Utility » ressemble à une vraie machine
- [x] N4 **Un réglage qui ne désigne plus rien est ignoré** — machine débranchée, file
      renommée : l'impression échouerait en nommant une machine absente
- [x] N5 essais : `DetectionImprimantesTests` (27), sur les noms RELEVÉS du poste

## O. Un onglet pour le matériel du poste ✅

- [x] O1 Section « Matériel de ce poste » dans les paramètres : dossier des bornes, liste
      des imprimantes reconnues avec CE QUI les a fait reconnaître, et un choix par rôle
- [x] O2 **On affiche toujours ce que la détection a trouvé**, même quand un réglage
      l'emporte : sans cela, on ne peut pas savoir si la case est vide parce que la
      détection marche ou parce qu'elle a échoué
- [x] O3 Les listes de rôle portent TOUTES les files, pas seulement les reconnues : une
      machine que la détection n'a pas su lire est justement celle qu'on vient désigner
- [x] O4 Le dossier choisi est vérifié TOUT DE SUITE : le découvrir au prochain démarrage,
      c'est une journée sans commandes de bornes
- [x] O5 `PosteSettings` (`config\poste.json`) — tout facultatif : vide, l'application se
      débrouille seule ; renseigné, l'opérateur a le dernier mot
- [x] O6 Contrôlé à l'écran : DiLand trouvé, les deux DE100 en minilab, l'Epson en
      agrandissements, la DS620 en sublimation, le Canon écarté

## Vérification

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1094 verts**

---

# Exécution — 14ᵉ passe (fin)

## I. Le format et le rapport sur la vignette ✅

- [x] I1 Badge de FORMAT sur la vignette de la planche, en haut à gauche : depuis qu'une
      commande mélange les formats, c'est la seule chose qui distingue à l'œil une 10×15
      d'une 15×20
- [x] I2 Absent tant qu'aucun format n'est posé — un badge vide ne dit rien
- [x] I3 Sans le PRIX, contrairement au bandeau : répété sur soixante vignettes il mange la
      place, et le total est déjà dans la barre du bas
- [x] I4 Dans « Modifier », la bande porte le format ET le rapport des côtés
      (`RatioLabel`, déjà là dans la planche) : on voit du même coup ce que le format va
      rogner

## J. Textes noirs sur fond bleu ✅

WPF donne aux `CheckBox`, `RadioButton`, `TextBox` et `PasswordBox` la couleur de texte du
SYSTÈME — du noir — et un fond blanc aux deux derniers. Sur nos panneaux bleu-gris, une
vingtaine de libellés étaient donc écrits en noir sur bleu.

- [x] J1 Styles IMPLICITES dans `App.xaml` : ces types portent leur couleur eux-mêmes
      (`Control.Foreground`), rien ne la leur transmet depuis le conteneur
- [x] J2 Sans danger, contrairement à un style implicite de `TextBlock` : ils ne paraissent
      jamais dans une liste au fond clair du système
- [x] J3 `CaretBrush` et `SelectionTextBrush` : sans eux le curseur est noir sur fond sombre
      — donc invisible — et le texte sélectionné reste noir sous le bleu de la sélection
- [x] J4 Les styles NOMMÉS de `TextBox` reprennent l'implicite (`BasedOn`) : c'est le piège
      déjà rencontré sur les listes déroulantes
- [x] J5 Contrôlé À L'ÉCRAN, pas seulement au code : capture des paramètres, avant/après
- [x] J6 essais : `ContrasteXamlTests` (4)

## K. Aperçu des photos dans « Commandes du jour » ✅

Un numéro et une heure ne disent rien d'une planche d'identité : quand un client revenait
chercher la sienne, il fallait ouvrir les commandes une à une.

- [x] K1 Les quatre premières photos de chaque commande, en vignettes de 76 px
- [x] K2 « +12 » quand il y en a plus : l'opérateur doit savoir qu'il n'a pas tout sous
      les yeux
- [x] K3 Chargées APRÈS l'affichage, en tâche de fond : lire les vignettes de toutes les
      commandes avant de montrer la liste la ferait attendre pour rien
- [x] K4 **Aucune boîte de dialogue quand les fichiers manquent** — au-delà de trente jours
      les photos partent à l'archive, et une alerte par commande archivée rendrait l'écran
      inutilisable. La ligne garde son numéro, sa date et ses boutons
- [x] K5 Contrôlé à l'écran : les planches d'identité se reconnaissent au premier coup d'œil

## Vérification

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1053 verts**

---

# Exécution — 14ᵉ passe (suite)

## G. « Pipe is broken » une impression sur deux ✅

**Ce n'était pas l'impression qui cassait le relais : le relais était DÉJÀ MORT quand elle
arrivait.** Trouvé dans les journaux du 05/08/2026 :

```
16:47:19  relais · Fatal error. Internal CLR error. (0x80131506)
             at System.Text.Json...EnumConverter`1[[...DnpStatusGroup]]..ctor(...)
16:47:39  Impression : commande 05-020 lancée
16:47:40  Impression : commande 05-020 en échec | Pipe is broken
```

Le relais répond à chaque commande dans son propre `Task.Run`. Le bandeau demande l'état du
minilab puis celui des DNP coup sur coup : les deux réponses faisaient construire le MÊME
convertisseur d'énumération par réflexion, en même temps. En 32 bits, le moteur d'exécution
n'y survit pas.

- [x] G1 Tous les convertisseurs du protocole sont résolus au CHARGEMENT de la classe, sur
      un seul fil, avant le premier message. `MakeReadOnly` ferme la porte derrière
- [x] G2 Les propriétés CALCULÉES ne traversent plus le tube (`[JsonIgnore]` sur
      `DnpStatus`) : le protocole transporte la donnée brute, jamais ses interprétations
- [x] G3 ⚠ Tout nouveau type transporté doit être ajouté à la liste du préchauffage, sinon
      il retombe sur la résolution paresseuse — donc sur la course

## H. Le multi-format ne survivait pas à la sélection ✅

Les clients commandent plusieurs formats dans une même commande — relevé sur la base :
10x15+10x10, 10x15+13x18, 10x15+15x20, 8x10+10x15, 13x18+18x24. Le format par photo était
bien posé à la réception, mais le seul fait de dérouler la liste « Produit » du bandeau
ramenait toutes les photos cochées au même.

- [x] H1 **La liste ne modifie plus rien à elle seule.** Le report est passé sur un bouton
      « Appliquer aux photos cochées », où il se voit et ne part pas tout seul
- [x] H2 Le bouton porte sur les photos COCHÉES : cinq photos en 15×20, les dix autres
      restent en 10×15
- [x] H3 Il reste éteint sans photo cochée — un bouton qui ne fait rien laisse croire que
      le format n'a pas été pris
- [x] H4 **Dans « Modifier », on vise à la CASE À COCHER.** Le Ctrl+clic faisait déjà cela
      et personne ne l'a trouvé : rien ne l'annonçait. Il continue de fonctionner
- [x] H5 La vignette de la bande affiche son FORMAT : sans lui, rien ne distingue une
      10×15 d'une 15×20, et le multi-format se pilotait à l'aveugle

## Vérification de la suite

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1049 verts**, dont les deux essais qui
      contrôlent les gestionnaires et les ressources du XAML

---

# Exécution — 14ᵉ passe

## A. Le liseré blanc sur TOUS les tirages ✅

Signalé sur des 10×15, des 13×18 et des 15×20 — donc sur tout ce qui sortait, et absent
auparavant. **La cause est la correction du 21×29,7 de la 13ᵉ passe.**

Le DE100 réclame l'image AVEC les 3 mm de débord qu'il rognera. `FitPageToRoll` étendait
donc le canevas à cette définition **en comblant de BLANC** : la photo se retrouvait cernée
d'un liseré d'un millimètre et demi, que le rognage de la machine ne mangeait pas — il part
du bord du PAPIER, pas du bord de l'image.

Relevé dans les journaux de la boutique du 05/08/2026, sur trois formats différents :

| Format demandé | Notre calcul | Ce que la machine réclame |
| --- | --- | --- |
| 152 × 102 mm | 1795 × 1205 px | **1830 × 1240 px** |
| 152 × 80 mm | 1795 × 945 px | **1830 × 980 px** |
| 152 × 180 mm | 1795 × 2126 px | **1830 × 2161 px** |

Le débord vaut +35 px sur chaque axe, quel que soit le format : ~3 mm à 300 ppp, donc
1,5 mm de blanc par bord.

- [x] A1 `RemplirLeDebord` : l'image est AGRANDIE jusqu'à couvrir la définition réclamée,
      puis rognée au centre. Le débord est rempli par la photo — c'est le sens du fond perdu
- [x] A2 **Le calage en deux temps** : d'abord les cotes NUES du tirage (c'est là, et là
      seulement, que du blanc entre — un 10×15 sur un rouleau de 210 laisse une bande de
      chaque côté, et elle est voulue), puis le débord machine par-dessus
- [x] A3 **Le facteur est le même sur les deux axes** : le débord vaut le même nombre de
      pixels en largeur qu'en hauteur, donc pas la même proportion. Cadrer chaque axe
      séparément étirerait la photo de quelques millièmes
- [x] A4 Sans débord — machine muette, format inconnu, repli sur notre calcul — la méthode
      ne touche à rien
- [x] A5 essais : `DebordMinilabTests` (5), dont un qui vérifie que les bandes blanches
      légitimes du rouleau SURVIVENT à la correction

## B. Le cadrage des commandes de bornes, pour de bon ✅

Le cadrage du client tombait toujours à côté, malgré les corrections des passes
précédentes. **L'« Angle » de DiLand n'est pas la rotation du client : c'est la rotation
TOTALE depuis le fichier brut, orientation EXIF comprise.**

Studio applique toujours l'EXIF d'abord (`ImagePipeline.RenderInto` appelle `AutoOrient`),
puis les quarts de tour. Reprendre l'angle tel quel les additionnait : une photo de
téléphone en portrait — EXIF 8, donc Angle 270 — était redressée par l'EXIF puis tournée de
270° DE PLUS. Elle partait couchée, et le recadrage du client, exprimé lui aussi dans le
repère redressé, tombait à côté.

Vérifié sur la base de la boutique, sur les 185 photos d'angle non nul :

- **183** ont un Angle égal à leur orientation EXIF ;
- **2** sont de vraies rotations faites à la borne — fichiers sans EXIF, tournés d'un quart.

- [x] B1 `QuartsDeTourResiduels` = angle DiLand − orientation EXIF. On SOUSTRAIT au lieu
      d'ignorer l'angle : ignorer ferait sortir de travers les deux photos vraiment tournées
- [x] B2 `OrientationExif` : lecteur du seul tag 0x0112, écrit à la main. Faire entrer
      Magick.NET dans `Studio.Store` pour six octets amènerait ONNX et OpenCV avec lui
- [x] B3 **L'orientation se lit sur la COPIE**, jamais sur l'original : DiLand passe au XOR
      les 1024 premiers octets des commandes traitées, c'est-à-dire l'en-tête EXIF lui-même
- [x] B4 Toute anomalie rend « déjà droite » : un fichier tronqué ne doit pas empêcher une
      commande de s'ouvrir, et c'est le comportement d'avant
- [x] B5 Le recadrage, lui, n'est PAS transposé : DiLand l'exprime dans le repère redressé,
      celui-là même où l'image se retrouve une fois la rotation juste appliquée
- [x] B6 essais : `OrientationBorneTests` (16), sur les cotes réelles d'un téléphone

## C. Le bandeau comptait les photos, pas les FEUILLES ✅

Une photo demandée en deux exemplaires part en UN tirage de deux copies (`PrintNum`), et le
bandeau annonçait « 1 / 1 » pendant que la machine en sortait deux.

- [x] C1 `PrintProgress.Verdicts` : le total est en FEUILLES, le nombre de réponses
      attendues voyage à part — le DE100 répond une fois par tirage, exemplaires compris
- [x] C2 `TirageTermine` compte les VERDICTS : attendre autant de réponses que de feuilles
      laisserait la commande affichée jusqu'au délai de garde

## D. La barre de statut qui n'avançait pas ✅

Elle n'avançait pas « parfois » : sur le minilab, elle **n'avançait jamais** en cours de
route. `De100JobTracker.Report` ne rend une issue que sur un statut DÉFINITIF ; tant que la
commande est `Printing`, aucun verdict n'arrive. L'affichage restait donc à « 0 / 30 »
plusieurs minutes avant de sauter à « 30 / 30 ».

- [x] D1 L'avancement se lit sur le COMPTEUR de la machine (`TotalPrintCount`), relevé au
      début du tirage puis toutes les 10 s. Lui monte feuille par feuille
- [x] D2 Deux fois moins souvent que le battement de la durée : chaque relevé traverse le
      relais 32 bits pendant que la machine travaille, et une 10×15 met une dizaine de
      secondes à sortir
- [x] D3 Borné au total et jamais décroissant : le compteur est global à la machine, et une
      commande lancée à côté depuis DiLand ne doit pas faire dépasser cent pour cent
- [x] D4 **Dégradation propre** : sans compteur lisible, l'affichage retombe sur les
      verdicts, comme avant. Un relais muet ne laisse jamais la barre plate

## E. Le rouleau choisi ne tenait pas ✅

`LoadMachinesAsync` remettait `PreferredMinilabMachine` à null et la liste sur
« Automatique » à CHAQUE ouverture de l'écran : un aller-retour suffisait à perdre le
rouleau qu'on venait de désigner.

- [x] E1 Le choix explicite de l'opérateur SURVIT à la navigation
- [x] E2 Une machine passée hors ligne entre-temps retombe sur « Automatique », préférence
      comprise — imposer une machine absente ferait refuser la commande en la nommant
- [x] E3 La règle de la 13ᵉ passe tient toujours : rien n'est imposé sans geste explicite

## F. Gestes de la grille ✅

- [x] F1 **Maj+clic prend toute la PLAGE** depuis la dernière photo touchée. Elle COCHE,
      elle ne bascule pas : basculer décocherait celles qui étaient déjà prises
- [x] F2 **« − » à un exemplaire RETIRE la photo** de la commande, au bouton comme au
      clavier. Il s'arrêtait à 1 sans rien faire, et il fallait deviner qu'on décochait la
      case pour retirer une photo

## Vérification de la 14ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **1049 verts** (955 à la 13ᵉ passe)
- [x] Le débord et l'orientation vérifiés sur les DONNÉES RÉELLES de la boutique : journaux
      d'impression du jour, et les 1216 images de la base DiLand

---

# Exécution — 13ᵉ passe

## Q. L'estimation, c'était le TEMPS ✅

Précision de l'exploitant : par « estimation », il entendait la DURÉE — pas un compte de
tirages restants selon les consommables. « En comprenant les maintenances possibles » =
pauses de la machine comprises dans le temps annoncé.

- [x] Q1 `EstimationDuree` : le bandeau annonce « Commande 04-045 — 12 / 24 photos sorties ·
      environ 5 minutes »
- [x] Q2 **La commande se chronomètre elle-même dès trois photos sorties** : la cadence du
      moment vaut mieux que toute moyenne — la machine peut être froide ou en maintenance
- [x] Q3 **Les maintenances sont DEDANS**, et ne sont pas modélisées à part : le débit est
      mesuré de bout en bout, les pauses y sont comprises. Les exclure donnerait une
      estimation toujours trop courte, la pire des deux
- [x] Q4 **En fonction du FORMAT** : un A4 ne sort pas à la cadence d'un 10×15
- [x] Q5 La précision affichée suit la précision réelle : « environ 5 minutes », arrondi à
      cinq au-delà de dix minutes, au quart d'heure au-delà d'une heure. « environ »
      disparaît quand le débit est mesuré
- [x] Q6 Le débit s'apprend commande par commande (`config/debits.json`), en moyenne
      pondérée et glissante ; valeurs aberrantes rejetées
- [x] Q7 Battement de 5 s dans le bandeau, sans quoi la durée resterait figée entre deux
      photos — vingt secondes sur un A4
- [x] Q8 essais : `EstimationDureeTests` (16)

⚠ **L'estimation par consommables de la 12ᵉ passe est CONSERVÉE** : elle a révélé le bac de
maintenance de la machine A à 95 % et le magenta de la B à 15 %. Les deux cohabitent — la
durée pour la commande en cours, les consommables pour la machine. Dites-le si la seconde
est de trop.

## R. Prévenir depuis l'écran des tirages, automatiquement ✅

- [x] R1 Bouton **« 🔔 Prévenir à la fin »** dans la barre des tirages : l'adresse se prend
      AVANT d'imprimer, pendant que le client est encore là
- [x] R2 Le bouton affiche l'adresse une fois posée — sinon rien ne distingue les deux états
- [x] R3 **Le message part à la fin du TIRAGE, pas de l'envoi.** Sur le minilab, envoyer
      trente tirages prend quelques secondes et les sortir plusieurs minutes : annoncer à
      l'envoi ferait venir le client devant une machine qui travaille encore
- [x] R4 **Jamais si un tirage a échoué** : on ne fait pas venir quelqu'un pour une
      commande incomplète
- [x] R5 **Une seule fois** (`Order.CustomerNotified`) : une réimpression n'enverra pas un
      second message
- [x] R6 L'adresse voyage avec la COMMANDE, pas avec l'écran : le message part bien après
      qu'on l'a quitté, et survit à un redémarrage
- [x] R7 Un courriel qui ne part pas ne ressemble pas à un échec d'impression : le tirage
      est sorti, et le message se rattrape depuis « Commandes du jour »

## Vérification de la 13ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **955 verts**
- [x] Bouton « Prévenir à la fin » contrôlé à l'écran des tirages

---

# Exécution — 12ᵉ passe

## O. Estimation de ce qui reste, maintenances comprises ✅

Le bandeau annonçait « ~576 × 10x15 » d'après le SEUL papier restant. Sur les machines de
la boutique, ce chiffre était un mensonge.

**Ce que l'estimation révèle, dès sa mise en service :**

| Machine | Avant | Maintenant |
| --- | --- | --- |
| A | ~575 × 10x15 | **~100 × 10x15 · bac de maintenance à 95 %** |
| B | ~298 × A5 | **~120 × A5 · magenta à 15 %** |

- [x] O1 `EstimationConsommables` : trois comptes — papier, encre, bac — et l'on retient le
      plus petit. Le résultat dit toujours ce qui LIMITE
- [x] O2 L'estimation porte sur le **format visé** (`DernierFormatMinilab`), pas sur le
      premier format du rouleau : annoncer des 10×15 à qui lance des A4 n'apprend rien
- [x] O3 Une encre sous 20 % est annoncée même quand elle ne limite pas encore : c'est le
      moment de commander la cartouche
- [x] O4 **La calibration s'apprend** — deux relevés (compteur + niveaux) donnent la
      consommation réelle de CETTE machine, rangée dans `config/consommables.json`.
      Garde-fous : 50 tirages d'écart minimum, et un niveau qui a bien BAISSÉ (une
      cartouche changée remonte)
- [x] O5 Tant que rien n'est observé, l'estimation s'annonce avec un tilde. Les valeurs par
      défaut sont prudentes : changer une cartouche trop tôt coûte quelques euros, une
      commande arrêtée au milieu coûte un client
- [x] O6 essais : `EstimationConsommablesTests` (12)

⚠ **À regarder tout de suite : le bac de maintenance de la machine A est à 95 %.** Elle
s'arrêtera dans une centaine de tirages. C'est exactement ce que cette estimation devait
faire apparaître.

## P. Prévenir le client que sa commande est prête ✅

- [x] P1 Bouton **« 🔔 Prévenir »** sur chaque commande de « Commandes du jour »
- [x] P2 **Aucune pièce jointe** : ce message annonce, il ne livre pas. Joindre les photos
      reviendrait à les donner sans les vendre — l'envoi des fichiers reste la prestation
      facturée du bouton « ✉ Envoyer »
- [x] P3 Le message est montré TEL QUE LE CLIENT LE LIRA avant l'envoi, et l'aperçu partage
      la même méthode que l'envoi : deux textes séparés finiraient par différer
- [x] P4 Le contenu est décrit en produits — « 2 × 15x20 » — et non en enveloppes ni en
      codes du catalogue : c'est ce que le client comprend
- [x] P5 Le bouton reste éteint tant que l'adresse n'est pas plausible et que le courriel
      n'est pas configuré ; le manque se dit à l'ouverture, pas au moment d'appuyer
- [x] P6 essais : `CommandePreteTests` (8)

## Vérification de la 12ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **939 verts**
- [x] Bandeau contrôlé à l'écran : les deux limites réelles s'affichent
- [x] Écran « Prévenir le client » contrôlé : résumé, aperçu vivant, bouton conditionné

---

# Exécution — 11ᵉ passe

## ✅✅ LE 21×29,7 : la vraie cause, enfin

**Le minilab refuse les images en NIVEAUX DE GRIS.** La photo d'essai — `Scan169` — est un
scan noir et blanc. Elle traversait tout le rendu en gardant son unique canal, et le DE100
la rejetait dix secondes après l'avoir acceptée, sans un mot.

Ce que le journal du relais, enfin drainé (passe 10), a permis de voir :

```
DE100 : commande 2 en erreur sur la machine B — format « 210x297 », 2100×2970, 0/2 sortis
DE100 : PIF_GetPrintInfo(0) a rendu BadParam
```

Puis la comparaison des deux PNG — celui que Studio envoie, et celui qui était sorti lors
de l'essai réussi :

| | Studio | l'essai qui sort |
| --- | --- | --- |
| définition | 2515 × 3543 | 2515 × 3543 |
| densité | 300 ppp | 300 ppp |
| **espace** | **Gray, 2 canaux** | **sRGB, 4 canaux** |

**Preuve** : le fichier même de Studio, converti en sRGB et rien d'autre, renvoyé à la
machine — **sorti du premier coup**.

- [x] `PrintOrchestrator.EnTroisCanaux` sur toute image partant au minilab
- [x] `FitPageToRoll` réécrit le fichier **même si sa taille est déjà juste**, quand
      l'image est grise — sinon elle partait telle quelle
- [x] **Le define PNG `color-type 2` est indispensable** : poser `ColorSpace` et
      `ColorType` ne suffit pas, le PNG réécrit en gris dès que tous les pixels le sont.
      Deux tentatives de correction ont échoué là-dessus
- [x] essais : `MinilabImageTests` (5), dont un qui échouerait si l'on retirait le define

⚠ **Ce qui explique aussi pourquoi le 18×24 sortait** : c'était une autre photo, en
couleur. Rien à voir avec le canal variable, contrairement à ce que la 9ᵉ passe supposait.
La correction de la DÉFINITION reste juste et nécessaire — elle est exigée en plus.

## Le relais mourait à cause de nous

- [x] `PIF_GetPrintInfo` était appelée DEPUIS la callback du SDK. L'indice 0 rend
      `BadParam` (le SDK compte à partir de 1), et surtout **le relais mourait quelques
      secondes après** — « Pipe is broken », commande 04-041. On n'appelle plus le SDK
      depuis une callback du SDK
- [x] Le callback se contente de `ST_ORDER_INFO`, qui portait déjà tout ce qu'il fallait
- [x] Ses cotes sont en **dixièmes de millimètre** : « 2100×2970 » se lit 210 × 297 mm

## Vérification de la 11ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **917 verts**
- [x] Conversion prouvée sur la machine réelle, avec le fichier de production

---

# Exécution — 10ᵉ passe

## I. Le relais DE100 se bloquait — LA cause de plusieurs pannes ✅

**Vingt-sept redémarrages dans la journée du 04/08/2026**, dont plusieurs « Pipe is
broken » en pleine session.

Le relais écrit tout son journal sur `Console.Error`, une ligne par commande traitée. Cette
sortie était REDIRIGÉE par l'application… et **jamais lue**, sauf en cas d'échec au
démarrage — autant dire jamais. Le tampon d'un tube anonyme fait quelques kilo-octets :
une fois plein, `WriteLine` **bloque le processus enfant**. Le relais cessait de répondre,
l'application voyait « Pipe is broken », le relançait, et le cycle recommençait.

Ce que ce seul défaut expliquait :

- la commande **04-040** partie sans jamais recevoir son verdict ;
- le **bandeau des machines** qui se vidait ;
- l'**actualisation très lente** par moments ;
- **les compteurs de tirages qui ne montaient plus** alors que les photos sortaient — les
  `JobFinished` se perdaient avec le relais.

- [x] I1 La sortie d'erreur est drainée EN CONTINU et déversée dans le journal, préfixée
      « relais · ». Le blocage disparaît, et ce que le relais a à dire arrive enfin
- [x] I2 Les quarante dernières lignes sont gardées pour le diagnostic de démarrage
- [x] I3 Encodage UTF-8 des deux côtés : « trouvé » arrivait en « trouvâ€š »

## J. Le bandeau perdait les deux DE100 ✅

Dans `RefreshAsync`, si la lecture du minilab échouait, sa liste restait vide — puis la
DNP écrasait **l'ensemble** des tuiles avec la sienne. Les deux Fuji disparaissaient.

- [x] J1 Chaque famille est relue de son côté et garde son DERNIER ÉTAT CONNU ; la barre
      est recomposée des deux. Une machine injoignable dix secondes n'a pas disparu de la
      boutique
- [x] J2 Le message le dit : « le bandeau montre son dernier état connu »

## K. « Vider la file » ne faisait rien ✅

`Operation is not valid due to the current state of the object` : `CancelAllJobs` était
invoqué sur une instance WMI issue d'une requête **à propriétés restreintes**, donc sans son
chemin complet — WMI refuse alors d'invoquer une méthode dessus.

- [x] K1 Chaque travail est supprimé par son chemin, un par un. Marche aussi quand un seul
      est fautif, et dit combien il en a réellement retiré
- [x] K2 Le bouton retrouve sa tuile par son `DataContext` et non par sa lettre : la barre
      recomposée entre-temps le faisait échouer en silence
- [x] K3 **Vérifié en conditions réelles** : deux travaux bloqués depuis 2 h 45 supprimés,
      file à zéro

## L. La réimpression qui ne disait rien ✅ (voir aussi G)

- [x] L1 `OnReprint` passe par `Impressions.Lancer` : avancement, issue et motif au bandeau

## M. Toutes les erreurs machine, et le geste qui va avec ✅

`ConduiteMachine` : pour chaque état des deux familles, ce que Studio FAIT et ce que
l'opérateur doit faire. Les états étaient traduits à trois endroits différents et **aucun
ne disait quoi faire**.

Cinq conduites : `Continuer`, `Patienter`, `MettreEnAttente`, `ViderLaFile`, `Arreter`.
Un état inconnu vaut `MettreEnAttente` — on préfère faire patienter une commande à tort que
la déclarer perdue.

| Situation | Conduite | Ce qu'on dit |
| --- | --- | --- |
| minilab prêt | Continuer | — |
| minilab en veille | Patienter | « elle se réveille toute seule au premier tirage » |
| minilab occupé / en cours | Patienter | — |
| erreur reprise possible | MettreEnAttente | « la commande repartira seule » |
| erreur machine arrêtée | Arreter | « intervenir avant de relancer » |
| tirage refusé AVEC motif | Arreter | le motif de la machine, mot pour mot |
| tirage refusé SANS motif | Arreter | « la définition ne correspond pas au format » |
| commande suspendue / minilab occupé | MettreEnAttente | « elle attend un geste sur la machine » |
| DNP muette au SDK | MettreEnAttente | « fermez DiLand : il tient le port USB » |
| papier / ruban épuisé | MettreEnAttente | « repartira seule après le changement » |
| panne matérielle DNP | Arreter | « éteindre-rallumer, puis SAV » |
| file en pause | MettreEnAttente | « relancez dans les fenêtres d'impression » |
| **file figée ≥ 10 min** | **ViderLaFile** | « videz la file, puis réimprimez » |

- [x] M1 Branchée sur le bandeau : l'état ET le geste, sur chaque tuile
- [x] M2 essais : `ConduiteMachineTests` (29)

⚠ **Le cas « file figée »** est le seul où la machine MENT : elle se déclare prête, ne
signale aucune erreur, et rien ne sort. C'est exactement ce qui a bloqué la DS620 deux
heures. Il l'emporte donc sur tout ce que la machine raconte.

## N. Écran de recadrage ✅

- [x] N1 Le cadre passe en **jaune** (`#FFD24A`), la couleur des poignées. Sur une photo
      claire, un cadre blanc se confond avec l'image
- [x] N2 **« Contour de découpe » fonctionnait**, mais rien ne le montrait : le trait
      n'existait qu'au rendu final, et la case ne rafraîchissait même pas l'aperçu. Le
      trait de découpe se voit maintenant sur la surface, et les vignettes se redessinent

⚠ Je n'ai pas reçu la photo mentionnée — le changement est fait d'après la description.

## Vérification de la 10ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **912 verts**
- [x] Journal du relais visible et lisible dans `app-*.log`
- [x] « Vider la file » essayé pour de bon sur la DS620 : 2 travaux supprimés, file à zéro
- [x] Bandeau contrôlé à l'écran : les trois machines, avec leur geste

## Reste à faire par l'exploitant (10ᵉ passe)

- [ ] **Refaire un 15×20 depuis « Commandes du jour »** : la réimpression passe désormais
      par le suivi, et le motif s'affichera si la machine refuse
- [ ] Surveiller si le relais se relance encore. S'il le fait, le journal en dira
      maintenant la raison — il n'en disait aucune

---

# Exécution — 9ᵉ passe

Six demandes de l'exploitant, 04/08/2026 (fin de journée), après la 8ᵉ passe ci-dessous.

## A bis. Le 21×29,7 après le changement de cyan — ce qui est ÉCARTÉ

Commande 04-029 (17:09) : refusée elle aussi, et `ST_PRINT_INFO.errmsg` est **vide**. La
machine refuse sans dire un mot. Sonde `DeviceProbe de100 formats` passée sur les deux
machines, en lecture seule :

| Ce qu'on soupçonnait | Ce que la machine répond | Verdict |
| --- | --- | --- |
| encre épuisée | Cyan **99**, Jaune 68, Magenta 16, Noir 27 | ❌ écarté |
| tirage trop long pour le rouleau | `PaperLengthMax` = **1000 mm** (il en faut 297) | ❌ écarté |
| file bloquée côté machine | `CntWaiting` = `CntPrnWaiting` = `CntPrnPrinting` = **0** | ❌ écarté |
| format inconnu du SDK | `PIF_DevGetPixelCount(210×297)` → **Ok**, 2515 × 3543 px | ❌ écarté |
| résolution inattendue | `Resolution` = **300**, celle qu'on envoie | ❌ écarté |
| machine en panne | état `Ready`, `MaintenanceOpe` = 0 | ❌ écarté |

**Ce qui reste : le `PrintSizeName`.** C'est le seul paramètre du tirage qui dépende de la
configuration DE LA MACHINE, et le seul qui diffère entre ce qui sort (« 152x102 »,
« 152x203 », des centaines par jour sur la machine A) et ce qui est refusé (« 210x297 »).
Le SDK n'expose aucune liste des noms déclarés — `DevPrintInfoParam.ini`, qui énumère tout
ce que `PHIF_GetValue` sait rendre, n'en contient pas —, donc cela ne se prouve que par un
tirage.

- [x] Le champ **« Nom du format au minilab »** est ajouté à la fiche produit
      (`Product.MinilabPrintSizeName` existait, il n'était saisissable nulle part). Vide =
      Studio le déduit du rouleau, comme aujourd'hui
- [x] `DeviceProbe de100 formats` : nouvelle sonde, en lecture seule, qui interroge la
      machine sur ses formats et ses propriétés
- [x] Le callback journalise désormais le format demandé, l'état de chaque tirage et le
      verdict de `PIF_GetPrintInfo`, même quand `errmsg` est vide
- [x] À défaut de motif, le verdict rendu à l'écran nomme le format demandé plutôt que de
      se taire

### ✅ LA CAUSE, trouvée par essais sur la machine — et corrigée

Ce n'était **pas** le nom du format. C'est la **DÉFINITION de l'image**.

`PIF_DevGetPixelCount` dit ce que la machine attend : **2515 × 3543 px** pour un 210 × 297
à 300 ppp. Studio envoyait **2480 × 3508** — les cotes nues. La machine ajoute son DÉBORD
de 3 mm (2515 px = 212,9 mm, 3543 px = 299,9 mm) et le canal FIXE `A4` l'exige au pixel
près, sans donner le moindre motif. Le 18×24 sortait avec le même écart parce qu'il passe
par un canal VARIABLE, tolérant.

Neuf essais de noms (« 210x297 », « A4 », « 21xL », cotes inversées, sans nom…) ont tous
échoué. **Ce qui a mis sur la voie : le TÉMOIN** — le format du 18×24 envoyé avec une image
au mauvais rapport a échoué lui aussi, alors qu'il sort tous les jours. Le format n'était
donc pas en cause, mais l'image.

Premier essai à la définition juste : **SORTI**.

- [x] `PrintOrchestrator.DefinitionAttendue` demande à la machine ce qu'elle attend, et
      `FitPageToRoll` cale l'image dessus
- [x] Repli sur notre calcul si elle n'en dit rien : machine muette, relais coupé, format
      inconnu — on ne perd jamais un tirage parce qu'une lecture a échoué
- [x] La demande passe par le relais 32 bits : commande `pixel-count`, `ExpectedPixels`
      sur `IMinilabPrinter`
- [x] `MinilabPrintSizeName` **remis à vide** sur les trois produits 210 × 297 : le nom
      n'y était pour rien, et le laisser aurait masqué la vraie règle
- [x] Un écart entre notre calcul et le sien est JOURNALISÉ : c'est ainsi qu'on verra venir
      le prochain format concerné
- [x] Deux sondes gardées : `DeviceProbe de100 essais` et `de100 definitions`

⚠ **Un refus ne coûte pas de papier**, et c'est vérifiable : pendant les neuf essais, le
compteur de la machine est resté à 53 800 et le métrage à 44,45 m. Les deux 18×24 sortis
juste avant, eux, avaient fait passer le compteur de 53 798 à 53 800 et consommé 55 cm.

### Ce que la base de DiLand avait appris en chemin

Le 18×24 sort de la MÊME machine sur le MÊME rouleau (commande 04-030, 17:13). C'est ce
fait qui a tout débloqué : le problème ne vient ni de la machine, ni du rouleau, ni du
code — il vient du FORMAT.

`OutputProfileChannel`, la table des canaux de sortie de DiLand (cotes en unités 96 ppp),
pour un rouleau de 210 mm :

| Canal | Longueur admise | Variable |
| --- | --- | --- |
| `21xS` | 50 → 210 mm | oui |
| `21xL` | 210 → 1000 mm | oui |
| **`A4`** | **297 mm exactement** | **NON** |

Studio envoie « 210x240 » pour le 18×24 : aucun canal fixe ne correspond, il tombe dans le
variable `21xL`, et il sort. Il envoie « 210x297 » pour le 21×29,7 : cette longueur
correspond **exactement** au canal FIXE `A4`, que la machine exige alors par son nom — et
qu'elle refuse sous tout autre nom, sans un mot.

- [x] `MinilabPrintSizeName` posé sur les trois produits 210 × 297 (`21x29-7`, `a4`,
      `bord-blanc-21x29-7`), dans le catalogue de la boutique et dans celui du dépôt.
      Sauvegarde : `products.json.avant-a4`

⚠ **« A4 » a été essayé et REFUSÉ** (commande 04-032, 17:34:57, même absence de motif).
Le catalogue porte donc maintenant **`21xL`**, le nom du canal lui-même — c'est l'essai
suivant. Si celui-là passe, la règle est que le DE100 attend le nom du CANAL et non celui
du format ; s'il échoue aussi, la piste du nom est morte et il faudra regarder ailleurs
(cotes envoyées, ordre largeur/hauteur, ou configuration de la machine B à faire ouvrir
par Fuji).
- [x] Le 18×24 n'est PAS touché : il dépend du canal variable, et il sort
- [x] essais : `MinilabRoutingTests` (+4) — le nom du produit l'emporte, et sans nom imposé
      on garde celui déduit du rouleau
- [x] `DiLandProbe sql <requête>` : lecture libre de la COPIE de la base DiLand. C'est
      elle qui a donné la réponse, et rien d'autre ne pouvait la donner

⚠ **Les autres rouleaux ont les mêmes canaux fixes** — 15x20 (203 mm) sur le rouleau de
152, par exemple. Rien n'a été changé pour eux : tout ce que la boutique tire aujourd'hui
sort, et on ne corrige que ce qui est cassé. Mais si un format se met à être refusé sans
motif, c'est la première chose à regarder ; la table complète est dans
`system_architecture.md`.

## G. La réimpression qui ne dit rien ✅

Réimpression de 04-032 depuis « Commandes du jour » : **elle a bien eu lieu**, avec le
format « A4 », et la machine l'a refusée à 17:34:57. Rien ne s'est affiché.

`OrdersView.OnReprint` appelait `PrintEnvelope` dans un `Task.Run` à elle, **hors du suivi
des impressions**. Deux conséquences :

1. `SuiviImpressions.TirageTermine` cherche le travail par son numéro de commande, n'en
   trouvait aucun, et **le verdict de la machine se perdait** ;
2. « Enveloppe réimprimée » s'affichait dès l'ENVOI, avant le moindre tirage — un message
   de réussite que rien ne permettait de tenir.

- [x] G1 La réimpression passe par `Impressions.Lancer`, comme une commande neuve :
      avancement, issue et motif de refus se lisent dans le bandeau
- [x] G2 Plus de boîte de dialogue de fin, qui ne pouvait dire que des choses fausses

## H. La DNP bloquée à 3 photos ✅

Trois travaux étaient restés dans la file Windows de la DS620, **aucun n'ayant imprimé une
seule page** :

| Travail | Origine | Soumis |
| --- | --- | --- |
| `Easyephoto` | DiLand | 15:42, il y a 115 min |
| `Easyephoto` | DiLand | 15:42, il y a 115 min |
| `Studio 04-026-1` | Studio | 16:09, il y a 88 min |

La machine se déclarait pourtant prête, sans erreur (`DetectedErrorState = 0`). Les deux
premiers ne viennent même pas de Studio.

- [x] H1 File vidée : la DS620 est repassée `PrinterStatus = 3` (prête) et le bandeau
      affiche « Prête à imprimer — rien dans la file »
- [x] H2 `DnpSpouleur.Vider` + bouton **« 🗑 Vider la file »** sur la tuile de la machine,
      visible dès qu'une file du spouleur a quelque chose en attente. Il fallait jusqu'ici
      passer par les fenêtres d'impression de Windows
- [x] H3 Une confirmation qui dit COMBIEN de photos ne sortiront pas — ce qui est supprimé
      ne revient pas

⚠ **La planche d'identité de la commande 04-026 n'est jamais sortie** : elle était dans les
travaux supprimés. À réimprimer depuis « Commandes du jour » si le client l'attend.

## A. Le 21×29,7 refuse toujours, cyan changé ✅ (outillé)

- [x] A1 **`PIF_GetPrintInfo` n'était appelée nulle part.** Elle est déclarée depuis le
      premier jour, et sa structure `ST_PRINT_INFO` porte un champ `errmsg` de **512
      caractères** : le motif du refus existait, personne n'allait le chercher. C'est pour
      cela que 04-015, 04-020 et 04-027 n'ont laissé que « erreur signalée par le minilab »
- [x] A2 Le motif est lu sur chaque tirage d'une commande en `Error`, joint au verdict,
      écrit au journal, et **affiché dans le bandeau**
- [x] A3 Les deux callbacks natifs sont désormais protégés : une exception qui remonte au
      SDK emporte le processus, donc le relais, donc le suivi de tous les tirages en cours
- [x] A4 essais : `De100JobTrackerTests` (+3)

⚠ **La cause reste à établir, et c'est maintenant possible.** Le changement de cartouche a
écarté les consommables sans rien apporter à sa place. Au prochain 21×29,7, la machine
dira ce qui ne va pas — en toutes lettres, à l'écran.

## B. Les messages d'erreur coupés ✅

- [x] B1 `PrintBannerText` : `TextTrimming="CharacterEllipsis"` + `MaxWidth="620"` coupaient
      la phrase à l'endroit où elle devient utile. Les trois textes concernés passent en
      `TextWrapping`
- [x] B2 Le bandeau passe en ALERTE dès qu'un tirage est refusé, sans attendre la fin de la
      commande : le fond vert sur une machine qui refuse est ce qui fait rater un incident

## C. Changer l'encre en cours de route sur 600 photos ✅

- [x] C1 `CadenceSpouleur` : avant CHAQUE page, on regarde la file. Machine en panne, en
      pause ou hors ligne → on s'arrête ; plus de trois pages en attente → on patiente ;
      sinon la page part. `PrintPages` déversait jusqu'ici toute la commande d'un trait
- [x] C2 **Le point de reprise compte ce qui est SORTI**, plus ce qui a été remis à
      Windows. Sur six cents photos, une panne à la troisième laissait partir les cinq cent
      quatre-vingt-dix-sept autres et le point disait « 600 »
- [x] C3 **La reprise REFAIT la dernière photo** : quand une machine s'arrête faute d'encre,
      celle qui était en cours sort pâle ou à moitié, et rien ne permet de le savoir depuis
      le logiciel
- [x] C4 On attend que la file se vide avant de déclarer l'enveloppe imprimée — elle passait
      « imprimée » cinq secondes après le premier tirage
- [x] C5 Une file que le spouleur ne décrit pas s'imprime comme avant, d'un trait : on ne
      bloque jamais une impression parce qu'une lecture WMI n'a rien donné
- [x] C6 essais : `CadenceSpouleurTests` (14). **Deux défauts attrapés par eux** — une file
      illisible faisait annoncer PLUS de pages sorties qu'il n'en était parti (la reprise
      aurait sauté une photo), et le compteur d'abandon ne repartait pas correctement

## D. La commande partie sur la DNP au lieu du minilab ✅

**Cause trouvée.** Le catalogue porte **deux produits nommés « 10x15 »** : `10x15`
(102 × 152, minilab) et `10x15-dnp` (105 × 156, DS620). Triés par surface, leurs deux
tuiles se suivent dans la grille, et seule une ligne de texte gris les distinguait. La
commande 04-024 porte bien `ProductCode: "10x15-dnp"` : elle est partie où on la lui a
demandée, sur la mauvaise tuile.

- [x] D1 La tuile de format porte la machine en **pastille colorée** — minilab bleu,
      sublimation violet, Epson vert
- [x] D2 La grille des photos affiche **« Sortie : … »** à côté du produit, même code
      couleur, posé dès l'ouverture de l'écran
- [x] D3 Le choix de machine du minilab **disparaît** quand le produit n'y va pas : il ne
      commandait rien et laissait croire le contraire
- [x] D4 Le blocage qui a suivi est traité par C1 : onze travaux d'un coup, dont deux en
      156×105 et neuf en 105×156, est exactement ce qui arrête une DS620

⚠ **La vraie correction vous appartient** : renommer l'un des deux produits au Catalogue
(« 10x15 DNP », par exemple). Le nom d'un produit est ce qu'il annonce au client, ce n'est
pas au code d'en décider.

## E. Envoi par courriel depuis les commandes du jour ✅

- [x] E1 Bouton « ✉ Envoyer » sur les planches d'identité de l'écran, **le même module**
      que l'écran identité (`MailSendView`) — même prix, mêmes messages prédéfinis, mêmes
      trois fichiers envoyés
- [x] E2 Ce sont les ORIGINAUX de la commande qui repartent, avec le cadrage et les
      corrections tels qu'ils ont été tirés
- [x] E3 Le bouton ne paraît que sur les planches d'identité : sur un paquet de soixante
      10×15, il n'aurait aucun sens et se cliquerait par erreur

## F. La photo trop petite au recadrage identité ✅

- [x] F1 De la place reprise partout : bande à 160 px (au lieu de 240), vignettes à 120,
      boutons des modules à 40, titre sans marge basse, barre de réglages resserrée
- [x] F2 **Bouton « ⛶ Agrandir »** : escamote la bande et les réglages pour ne garder que
      la photo. Mesuré à l'écran — la scène passe de 420 px de haut à **705**, soit deux
      tiers de plus. Échap ou un second appui pour revenir
- [x] F3 Rien n'est perdu en chemin : repères, cadrage et réglages vivent dans la photo
      courante, le mode ne fait que cacher des panneaux

## Vérification de la 9ᵉ passe

- [x] `dotnet build` : 0 erreur — `dotnet test` : **879 verts**
- [x] contrôlé à l'écran : badge « Sortie », message d'erreur sur deux lignes, écran
      identité compacté, mode agrandi, bouton « ✉ Envoyer » sur les planches

## Reste à faire par l'exploitant (9ᵉ passe)

- [ ] **Relancer un 21×29,7.** Le catalogue envoie désormais « A4 » et non « 210x297 ».
      Si la machine refuse encore, le bandeau dira ce qu'elle en dit — et l'essai suivant
      est `21xL` dans `Catalogue → 21x29,7 → Nom du format au minilab`
- [ ] **Renommer le produit `10x15-dnp`** au Catalogue, pour qu'aucune tuile ne porte le
      même nom qu'une autre
- [ ] Sur la prochaine grosse commande, laisser volontairement l'encre s'épuiser une fois :
      vérifier que la commande part en attente, et qu'elle reprend bien à l'avant-dernière
      photo après le changement

---

# Exécution — 8ᵉ passe

Sept demandes de l'exploitant, 04/08/2026 (soir). La 7ᵉ passe est commitée (`eb0999f`) ;
son reste à faire est repris en fin de document.

## 1. Le 21×29,7 refusé alors que le bon rouleau est chargé ✅

**Deux défauts distincts, et un fait de machine.**

- [x] 1a **Le défaut logiciel.** `PhotoGridView.LoadMachinesAsync` posait
      `MachineCombo.SelectedIndex = 0` à chaque ouverture de la grille, ce qui posait
      `PreferredMinilabMachine = "A"` — donc un choix IMPOSÉ, que la sélection par rouleau
      de la 7ᵉ passe ne discute pas. Le 21×29,7 restait refusé « le rouleau chargé dans la
      machine A fait 152 mm » alors que le rouleau de 210 tournait dans la machine B.
      Commandes 04-010 (11:32), 04-014 (12:20) et 04-019 (14:50)
- [x] 1b Première ligne **« Automatique — selon le rouleau »**, retenue par défaut, qui
      remet `PreferredMinilabMachine` à `null`. Un choix explicite reste imposé
- [x] 1c Le refus nomme désormais la machine voisine qui porterait le format
      (`MachineQuiPorteLeFormat`) : « La machine B porte du 210 mm : choisissez-la dans la
      liste Minilab, ou repassez-la sur Automatique ». Il envoyait jusqu'ici changer un
      rouleau qui tourne déjà à deux mètres
- [x] 1d **Le second défaut : la machine ne disait pas ce qui n'allait pas.**
      `De100BridgePrinter.MachineEvent` n'était exposé nulle part — le relais transmettait
      ces événements depuis toujours sans un seul abonné. `AppServices` s'y abonne : tout
      au journal, les erreurs actives au bandeau
- [x] 1e essais : `MinilabRoutingTests` (+4), sur `MachineQuiPorteLeFormat`

⚠ **Un fait relevé sur la machine, à traiter par l'exploitant.** Sonde du 04/08/2026 :

| | Machine A | Machine B |
| --- | --- | --- |
| rouleau | 152 mm Glossy | **210 mm Lustre** |
| état | Sleep | Ready |
| papier restant | 59,7 m | 45,0 m |
| encres | J 43 · M 42 · C 43 · N 38 | J 68 · **M 16** · **C 1** · N 27 |
| bac de maintenance | 95 | **38** |

Les commandes 04-015 (12:22) et 04-020 (14:51) sont bien PARTIES sur la machine B, et
c'est la machine qui les a refusées — « ÉCHEC · erreur signalée par le minilab ». **Le
cyan de la machine B est à 1 %.** C'est l'explication la plus probable de ces deux échecs,
et aucune correction logicielle ne la lèvera. Le motif exact sera au journal à la
prochaine tentative, grâce à 1d.

## 2. Annuler l'éclaircissement (identité et E-Photo) ✅

- [x] 2a `PrintExposure` remise à **0** sur `ID-FR-6` et `e-photo-dnp`, dans le catalogue
      de la boutique (`D:\PhotoStudioData\catalog\products.json`, sauvegarde
      `products.json.avant-annulation-exposition`) et dans celui du dépôt
- [x] 2b Le mécanisme reste en place et réglable à la fiche produit — l'écart de machine
      est réel, il se remesurera sur des tirages. Aucun produit ne l'utilise
- [x] 2c `system_architecture.md` le dit en tête de section, pour qu'on ne le remette pas
      par inadvertance

## 3. Le chargement des photos est trop long ✅

**Mesuré, pas deviné.** Vingt photos de 6 Mo de la commande 08-012 :

| | avant | après |
| --- | --- | --- |
| vignettes de la grille (512 px) | 3 557 ms | **2 378 ms** (−33 %) |
| aperçu « Modifier » / « Recadrer » (2048 px) | 10 037 ms | **4 425 ms** (−56 %) |
| second parcours des fichiers, cache chaud | 33 ouvertures | **0** |

- [x] 3a **L'indication de taille au décodeur JPEG valait le DOUBLE de la vignette
      demandée.** Le décodeur ne réduit que par 1/2, 1/4, 1/8 et prend le premier facteur
      qui reste au moins aussi grand que l'indication : à 4096 pour un aperçu de 2048, il
      ne pouvait plus réduire du tout et dépliait les 39 Mpx. La vignette produite est à un
      kilo-octet près la même
- [x] 3b `ThumbnailService.Lire()` rend la vignette **et la définition de l'original**, en
      une seule ouverture. La grille la demandait à `ImagePipeline.GetOrientedSize`, soit
      un second parcours du fichier — payé même quand la vignette était en cache : rouvrir
      un dossier déjà vu touchait les trente-trois originaux pour rien. Sur une carte SD,
      ce qui coûte est d'ouvrir le fichier
- [x] 3c La définition voyage dans le cache (fichier compagnon `.dim`). Un compagnon
      absent — les 4 300 vignettes déjà en cache — fait retomber sur l'original une fois,
      puis le dépose. Rien à purger
- [x] 3d **Effet de bord favorable** : `GetOrientedSize` passait par `Ping`, qui ne charge
      pas les profils, et ne voyait donc JAMAIS l'orientation EXIF. La lecture complète, si
- [x] 3e essais : `ThumbnailCacheTests` (+3), `IndexSheetRenderTests` recalé sur les `.jpg`

## 4. Commandes du jour : borne ou opérateur ✅

- [x] 4a Deux onglets de plus, sous « Tirages photo » : **« ↳ opérateur »** et
      **« ↳ borne »**, avec leurs compteurs
- [x] 4b Une pastille d'origine sur chaque carte — « Comptoir » en accent, le nom de la
      borne en violet. C'est ce qu'on cherche des yeux dans la liste « Tout »
- [x] 4c La règle est prise à l'ENVERS : tout ce qui n'est pas `"Operateur"` vient d'une
      borne. `Source` est une chaîne libre et les bornes s'y nomment de plusieurs façons
      (`borne` pour une reprise DiLand, `Borne1` pour une borne Studio) ; il n'y a en
      revanche qu'une façon d'être l'opérateur, et c'est ce code qui l'écrit
- [x] 4d Le numéro de commande n'affiche plus la source en toutes lettres : la pastille le dit

## 5. Ranger les modules de photo d'identité ✅

- [x] 5a Quinze contrôles alignés à la file dans un `WrapPanel` deviennent **quatre modules
      encadrés et titrés** : PAPIER, QUANTITÉ, REDRESSER, IMAGE
- [x] 5b « Photos » et « Planches » sous un même titre : ils se ressemblent et ne disent pas
      la même chose, les séparer les rendait indiscernables
- [x] 5c Un module se replie ENTIER quand la fenêtre rétrécit, là où le `WrapPanel` coupait
      un compteur en deux lignes
- [x] 5d Styles `Module`, `TitreModule`, `BoutonModule` — les dimensions des neuf boutons
      ronds tenaient en neuf recopies

## 6. Le QR WiFi se configure dans Paramètres ✅

- [x] 6a Nom du réseau, clé, sécurité (WPA / WEP / ouvert), nom masqué. Écrit
      `config\wifi.json`, qu'il fallait jusqu'ici connaître et éditer à la main — donc rien
      ne s'affichait sur les autres postes opérateur
- [x] 6b **Aperçu du code à côté**, fabriqué sur ce qui est à l'écran, avant enregistrement :
      on le scanne avec son propre téléphone. Même raison que le message d'essai du courriel
- [x] 6c La clé est en clair : c'est une clé qu'on donne aux clients. La masquer obligerait
      à la ressaisir en aveugle pour corriger une faute de frappe
- [x] 6d `AppServices.SaveWifi` remplace l'objet en mémoire, pas seulement le fichier —
      sans quoi le code aurait gardé l'ancien réseau jusqu'au prochain lancement
- [x] 6e Le profil de Windows n'est lu qu'UNE fois par ouverture de l'écran : `netsh` et un
      export de profil coûtent une seconde, et la réponse ne change pas pendant qu'on
      remplit un formulaire

## 7. Menus déroulants accordés au thème ✅

- [x] 7a Styles IMPLICITES `ComboBox` et `ComboBoxItem` dans `App.xaml` : les vingt-neuf
      listes en héritent sans qu'on les touche
- [x] 7b `LargeFormatPrintView` avait un style local `Cbo` ; **un style nommé ne reprend pas
      le style implicite** — `BasedOn="{StaticResource {x:Type ComboBox}}"` ajouté, sans
      quoi ses dix-sept listes seraient restées grises
- [x] 7c **Deux défauts vus à l'écran, et à l'écran seulement** — le XAML décrit une
      intention, pas un résultat :
      - le chevron passait SOUS le libellé. Il ne suffit pas de le poser à droite dans la
        grille : le texte doit vivre dans un `Border` qui RÉSERVE sa place (30 px) ;
      - le nom **`PART_Popup`** n'est pas décoratif. `ComboBox` le cherche dans son
        gabarit, et c'est ce qui déclenche le calcul de `SelectionBoxItemTemplate`. Sous un
        autre nom, la ligne fermée retombe sur le `ToString()` de l'objet : la liste des
        papiers de l'écran identité affichait
        « ProductChoice { Product = Studio.Core.Domain.Product, Capacite = 8, … } »

## 8. Bouton « Accueil » dans tous les onglets ✅

- [x] 8a Dans l'en-tête, à côté de « Retour » : un seul endroit, tous les écrans
- [x] 8b Il met de côté ce qui est en préparation, puis revient. Le travail est cherché dans
      toute la PILE (`Reprises.Trouver`) : depuis le recadrage d'une photo, c'est la grille
      qui porte la commande, deux écrans plus bas
- [x] 8c **Il ramène toujours à l'accueil**, même si l'enregistrement échoue. Un opérateur
      coincé sur un écran parce qu'un fichier ne s'écrit pas serait le pire des deux maux
- [x] 8d **Les planches d'identité se mettent de côté aussi** — `IdentiteEnAttente` :
      norme, photos, repères de crâne et de menton, cadrage, redressement, corrections,
      photo affichée. Elles se reprennent dans l'écran d'IDENTITÉ, jamais dans la grille
- [x] 8e L'entrée est soldée à l'impression de la planche (`IdSheetRecapView`, `attenteId`) :
      la laisser ferait proposer « Reprendre » sur une planche déjà tirée
- [x] 8f Rien n'est annoncé à l'écran : l'accueil montre déjà « En attente » avec l'heure.
      Une boîte de dialogue à chaque retour serait un clic de plus, cinquante fois par jour
- [x] 8g Une grille vide n'est pas mise de côté ; le mode borne n'a ni « Retour » ni « Accueil »
- [x] 8h essais : `TravailEnAttenteTests` (+2), sur la persistance de la section identité

## Vérification

- [x] `dotnet build` de la solution : **0 erreur**, seuls les 8 CS9057 d'OpenCvSharp, antérieurs
- [x] `dotnet test` : **862 verts**, 0 échec
- [x] Mesures de la passe 3 refaites sur les photos réelles de la commande 08-012
- [x] État des deux machines relevé par `DeviceProbe de100` sur la machine réelle
- [x] `system_architecture.md` mis à jour (6 sections)

- [x] application relancée : fenêtre ouverte, relais DE100 connecté, serveur d'envoi sur
      8123, aucune exception au démarrage
- [x] contrôlé à l'écran (`Capturer-Ecran.ps1`) : bouton « Accueil » présent dans
      l'en-tête, quatre onglets de « Commandes du jour » avec leurs pastilles d'origine,
      section WiFi de Paramètres avec son code QR, quatre modules de l'écran identité,
      menus déroulants sombres et libellés justes

## Ce qui n'est PAS couvert par les essais

Tout ce qui se clique — `Studio.App` n'est pas référencé par la suite. Ce qui a été vu à
l'écran est coché ci-dessus ; **reste à contrôler en situation** :

- [ ] la liste « Minilab » de la grille s'ouvre sur **« Automatique — selon le rouleau »**,
      et un 21×29,7 part tout seul sur la machine B
- [ ] les menus déroulants dans « Tirer sur l'Epson » (dix-sept listes, style `Cbo`)
- [ ] le bouton « Accueil » depuis un écran de recadrage : la commande doit réapparaître
      sur l'accueil, et « Reprendre » la rouvrir telle quelle
- [ ] une planche d'identité mise en attente puis reprise : repères et cadrage conservés,
      la détection de visage ne doit PAS se relancer
- [ ] une photo d'identité et une E-Photo : elles doivent ressortir comme AVANT
      l'éclaircissement
- [ ] une photo prise à la verticale : sa tuile doit annoncer « 4000 × 6000 » et non
      l'inverse (effet de bord de 3d, non couvert par les essais)

## Reste à faire par l'exploitant

- [ ] **La machine B est à 1 % de cyan** (et 16 % de magenta, bac de maintenance à 38 %).
      C'est la cause la plus probable des refus des commandes 04-015 et 04-020. À changer
      avant le prochain 21×29,7
- [ ] Reste ouvert des passes précédentes : une commande de 4 photos ou plus sur le DE100 et
      COMPTER ce qui sort ; T dans « Modifier » et dans identité ; Ctrl+A puis « Remplir » ;
      un PDF de plusieurs pages ; `Paramètres → Envoi par courriel` puis message d'essai ;
      tirage POLA réel ; les deux commandes de bornes absentes de la base de DiLand
      (#12360 du 18/06 et #6830 du 25/06)
