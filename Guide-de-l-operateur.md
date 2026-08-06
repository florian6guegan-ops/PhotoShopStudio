# Studio Photo — guide de l'opérateur

Ce guide s'adresse à quelqu'un qui va **se servir** du logiciel au comptoir, pas à
quelqu'un qui va le modifier. Il suit l'ordre d'une vraie journée : installer, servir un
client, tirer, suivre, et savoir quoi faire quand une machine fait des siennes.

---

## 1. Installer

Studio Photo s'installe **sans rien d'autre**. Pas de runtime .NET à poser, pas de base de
données à créer.

1. Télécharger l'archive `StudioPhoto-x.y.z.zip` depuis la page des versions du projet.
2. La décompresser où l'on veut — un dossier sur `C:` convient très bien.
3. Lancer `Studio.App.exe`.

Au premier lancement, le logiciel crée tout seul son dossier de travail : catalogue par
défaut, dossiers de commandes, journaux. Il le pose dans `D:\PhotoStudioData` si le poste a
un disque `D:` inscriptible, sinon dans le dossier local de l'utilisateur.

> **Pour imposer un autre emplacement** — un disque de données, un dossier partagé — définir
> la variable d'environnement `STUDIO_DATA` avant de lancer l'application.

### Ce qu'il faut installer en plus, selon les machines

| Machine | Ce qu'il faut |
|---|---|
| **Minilab Fuji DE100** | DiLand Studio installé sur le poste : le SDK Fuji y vit |
| **DNP DS620** | Le pilote DNP **et** DiLand (pour `cspstat.dll`) |
| **Imprimante grand format** | Son pilote Windows, rien de plus |
| **Aucune machine** | Studio démarre quand même : on prépare les commandes, on n'imprime pas |

Studio cherche les SDK tout seul dans `C:\Program Files (x86)\DiLand Studio 2`. Si DiLand
est ailleurs, on l'indique dans **Paramètres**.

---

## 2. Le premier réglage — Paramètres

À faire une fois, sur chaque poste. Tout y est facultatif : laissé vide, Studio se
débrouille.

**Imprimantes.** Studio reconnaît les machines branchées et leur donne un rôle. Si une
machine n'est pas reconnue, on la désigne à la main dans les listes « grand format » et
« sublimation ».

**Dossier de DiLand.** Utile seulement si l'installation n'est pas à l'endroit habituel.

**Supports de photos.** Coche les supports qu'on ne veut **pas** voir proposés quand on
choisit des photos — typiquement une clef de licence branchée en permanence, qui ne contient
jamais de photos. Un support coché disparaît des tuiles mais reste accessible par
« Parcourir ».

**Favoris.** Les dossiers d'où les photos arrivent chez vous : WeTransfer, courriel,
téléchargements. Ils apparaîtront comme raccourcis au moment de choisir les photos.

**Cadrage sur le visage.** Décoché par défaut. Coché, le cadre se pose sur le visage trouvé
au lieu du centre de la photo à l'ouverture de « Modifier ». Il ne touche jamais un cadrage
déjà posé.

**Adresse de rapport.** L'adresse à qui envoyer les journaux quand quelque chose ne va pas.
Elle ne se saisit qu'une fois.

---

## 3. Servir un client

### Choisir les photos

L'écran de départ propose trois choses : les **supports branchés** (carte SD, clef), les
**favoris** du poste, et **Parcourir**.

Dans le parcours des dossiers, chaque sous-dossier montre **combien de photos il contient**
avant qu'on l'ouvre — on ne tombe plus sur un dossier vide. Les dossiers sont rangés **du
plus récent au plus ancien**, ce qui met en tête ce qui vient d'arriver ; le bouton de tri
bascule vers l'ordre alphabétique.

Le client peut aussi **envoyer ses photos depuis son téléphone** : Studio affiche un QR code,
le téléphone se connecte au même réseau et dépose les fichiers.

### Choisir le produit et le format

On coche les photos, on choisit une famille de produits puis un format. Le catalogue porte
les prix, les tailles, les finitions et la machine de destination : c'est lui qui décide où
part le tirage, pas l'opérateur.

Pour un format qui n'est pas au catalogue, **Personnalisé** permet de saisir des dimensions
libres.

### Recadrer et corriger

**Modifier** ouvre la photo avec l'aperçu à gauche et les réglages à droite.

*Recadrer* — on fait glisser la photo, la molette zoome, et les **poignées du cadre** se
tirent vers l'extérieur pour montrer plus de photo. Clic droit : pivoter le cadre.
Touche `T` + molette : redresser de quelques degrés. Le bouton bascule entre **Remplir le
format** (la photo couvre tout, on en coupe les bords) et **Photo entière** (rien n'est
coupé, des marges blanches apparaissent).

*Contour de découpe* — trace un filet noir sur le bord du tirage, à suivre aux ciseaux quand
la photo sort avec des marges blanches.

*Corriger* — exposition, contraste, température, noir et blanc, yeux rouges. « Appliquer à
toute la sélection » propage le réglage à toutes les photos cochées.

### Photos d'identité

Parcours à part, conçu pour aller vite : on choisit le **document** (carte d'identité,
passeport, permis…), Studio détecte le visage et pose le cadrage **aux cotes réglementaires**.
Un gabarit de contrôle montre si la tête est à la bonne hauteur et à la bonne taille. On
imprime une **planche** de 6 photos, avec un récapitulatif avant d'envoyer.

---

## 4. Imprimer

Le bouton **Imprimer** envoie la commande et rend la main : le tirage part en tâche de fond,
on peut servir le client suivant.

**Ce qui se passe selon la machine :**

- **Minilab DE100** — la commande part **entière**, en une seule fois. Studio attend le
  verdict de la machine pour chaque tirage et le note.
- **DNP DS620** — depuis la version 1.2.0, Studio envoie l'image **directement à la machine**,
  sans passer par le pilote Windows. C'est ce qui a supprimé les décalages de couleur. Si
  l'envoi direct n'est pas possible, le tirage repart par le pilote et le journal le dit.
- **Grand format** — file d'attente dédiée, avec sa propre page de suivi.

**Rien ne repart jamais tout seul.** Si une impression n'a pas confirmé sa sortie — coupure,
machine arrêtée, application fermée en pleine commande — Studio le **signale** au démarrage
suivant mais ne réimprime rien. La décision se prend depuis « Commandes du jour », là où l'on
voit ce qu'on renvoie. Cette règle vient d'un incident réel : 58 tirages sortis en double.

---

## 5. Le bandeau des machines

En bas de l'écran, une tuile par machine : son état, son papier, ses encres.

| Ce qu'on lit | Ce que ça veut dire |
|---|---|
| **Prête** | Elle accepte un tirage |
| **En cours d'impression** | Elle travaille |
| **Intervention nécessaire** | Capot, bourrage, rouleau — la tuile dit quoi toucher |
| **En veille** | Elle dort ; le premier tirage la réveille |
| **Hors ligne** | Elle ne répond plus du tout |
| **longueur de rouleau non déclarée** | Le minilab ne sait pas combien de papier il lui reste : la longueur n'a pas été saisie **sur la machine** au chargement du magasin |

Cliquer sur une tuile ouvre le détail : compteurs, rouleau, encres, tirages restants par
format.

---

## 6. Suivre et retrouver

**Commandes du jour** garde sept jours d'historique, avec des onglets : *Tout*, *Tirages
photo* (au comptoir ou venus d'une borne), *Photos d'identité*. Une commande qui contient
des photos d'identité n'apparaît pas dans « Tirages photo » — elle a son propre onglet.

De là on peut **rouvrir les photos** d'une commande, la **réimprimer**, ou la **renvoyer au
client**.

**Envoyer au client** dépose les photos sur Dropbox et rend un lien à donner ou à envoyer par
courriel. Les fichiers sont effacés automatiquement au bout de quelques jours — la durée
annoncée au client est celle du ménage, pas celle du lien.

**Commandes des bornes** — si vous avez des bornes DiLand, Studio lit les commandes qu'elles
déposent, **sans les prendre à DiLand** : il travaille sur une copie. Produits, quantités et
recadrages faits par le client sont repris tels quels.

**Statistiques** — ce qui a été vendu, sur la période qu'on choisit.

---

## 7. Quand ça ne va pas

**Une machine n'apparaît pas.** Vérifier qu'elle est allumée et branchée, puis que DiLand est
installé (c'est lui qui porte les SDK). Une machine en veille se réveille au premier tirage.

**Un tirage n'est pas sorti.** Regarder le bandeau : la machine dit presque toujours pourquoi.
Rien n'est réimprimé automatiquement ; la reprise se fait depuis « Commandes du jour ».

**L'application ne démarre pas.** Le message nomme le dossier de données qu'elle n'a pas pu
créer. Vérifier les droits d'écriture, ou définir `STUDIO_DATA` sur un dossier accessible.

**Autre chose.** Le bouton **Envoyer un rapport** dans Paramètres expédie les journaux à
l'adresse configurée. Il dit exactement ce qu'il contient avant de partir, et **rien ne part
tout seul** : le poste travaille sur des photos de clients.

---

## 8. Les mises à jour

Studio vérifie s'il existe une version plus récente et l'annonce dans **Paramètres**. Rien ne
s'installe sans qu'on le demande — une mise à jour ferme l'application, éventuellement au
milieu d'une commande.

---

## Ce qu'il faut retenir

- **Rien ne se réimprime tout seul.** Un doute est signalé, jamais résolu à votre place.
- **Le catalogue décide de la machine**, pas l'opérateur.
- **Les photos du client sont recopiées** dès la création de la commande : il peut reprendre
  sa carte immédiatement.
- **Les réglages sont propres à chaque poste.** Le vôtre n'a pas à ressembler à celui de la
  boutique voisine.
