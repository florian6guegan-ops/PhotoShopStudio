# Studio Photo — installation, et ce qui change par rapport à DiLand

Ce document s'adresse à quelqu'un qui **tient déjà un labo sous DiLand**. Il ne raconte pas
le métier : il dit comment poser le logiciel, et ce qu'on gagne à passer par lui.

**Studio ne remplace pas DiLand du jour au lendemain, et il n'a pas à le faire.** Les deux
tournent en même temps sur le même poste, sur les mêmes machines. On s'en sert pour ce qui
va mieux, on garde DiLand pour le reste.

---

## 1. Installer

Il n'y a rien à préparer : ni runtime .NET, ni base de données, ni service à déclarer.

1. Télécharger `StudioPhoto-x.y.z.zip` sur la page des versions.
2. Décompresser dans un dossier — `C:\StudioPhoto` fait très bien l'affaire.
3. Lancer `Studio.App.exe`.

Au premier démarrage, le logiciel crée son dossier de travail tout seul. Il le pose dans
`D:\PhotoStudioData` si le poste a un disque `D:` inscriptible, sinon dans le dossier local
de l'utilisateur. Pour l'imposer ailleurs : variable d'environnement `STUDIO_DATA`.

### Le catalogue arrive avec le logiciel

**Il n'y a pas de catalogue à saisir.** Le premier démarrage pose celui de la boutique
d'origine : les formats, les prix, les paliers de remise, et surtout le **canal de machine**
de chaque produit — c'est lui qui décide qu'un 10×15 part au minilab et une planche
d'identité à la DS620.

Deux choses à faire ensuite, dans l'écran **Catalogue** :

- **Reprendre les prix.** Ce sont ceux d'un autre magasin. Ils n'ont aucune raison d'être
  les vôtres.
- **Réimporter les profils ICC.** Ils ne sont pas livrés — ce sont des fichiers du
  fabricant, parfois lourds. *Catalogue → Importer* va les chercher dans le dossier couleur
  de Windows, où les pilotes les ont déjà posés.

**Une mise à jour n'écrase jamais le catalogue d'un poste qui tourne.** Vos prix, vos
formats et vos réglages pilote vous appartiennent : la pose n'a lieu que si le poste n'a
pas encore de catalogue.

> Si vous voyez seulement cinq produits, dont quatre sur « Microsoft Print to PDF », c'est
> que le catalogue n'a pas été livré avec votre archive — cas des versions antérieures à la
> 1.3.2. Rien ne sortira des machines tant que ce sera le cas : reprenez une archive plus
> récente.

### Ce qu'il faut avoir à côté

| Pour piloter | Il faut |
|---|---|
| **Minilab Fuji DE100** | DiLand installé — c'est lui qui porte le SDK Fuji |
| **DNP DS620 / DS820 / QW410** | Le pilote DNP **et** DiLand (pour `cspstat.dll`) |
| **Imprimante grand format** | Son pilote Windows |

Autrement dit : **on n'enlève pas DiLand**. Studio va chercher ses bibliothèques dans
`C:\Program Files (x86)\DiLand Studio 2`. Si l'installation est ailleurs, on l'indique dans
*Paramètres*.

Sans DiLand, Studio démarre quand même : on prépare les commandes, on ne pilote simplement
pas ces machines-là.

### Les cinq minutes de réglage

Dans **Paramètres**, une fois par poste — tout est facultatif, laissé vide Studio se
débrouille :

- **Imprimantes** : les machines reconnues et leur rôle. Une machine que la détection ne lit
  pas se désigne à la main.
- **Supports de photos** : cocher ceux qu'on ne veut **pas** voir proposés — une clef de
  licence branchée en permanence, un disque de travail.
- **Favoris** : les dossiers d'où les photos arrivent chez vous (WeTransfer, courriel,
  téléchargements). Ils deviennent des raccourcis au moment de choisir.
- **Adresse de rapport** : à qui envoyer les journaux le jour où quelque chose cloche.

### Les mises à jour

Studio annonce lui-même les nouvelles versions dans *Paramètres*. **Rien ne s'installe sans
qu'on le demande** : une mise à jour ferme l'application, éventuellement au milieu d'une
commande.

---

## 2. Ce que Studio fait et que DiLand ne fait pas

### On peut annuler un tirage déjà parti

Le geste qui manque le plus. Une commande envoyée par erreur, un mauvais format, un client
qui change d'avis : Studio **rappelle la commande dans le minilab** tant que la machine ne
l'a pas tirée. Chez DiLand, une fois parti, c'est parti.

### La DS620 n'imprime plus par le pilote Windows

Comme DiLand, Studio envoie l'image **directement à la machine**. C'est ce qui supprime les
décalages de couleur qu'on voit quand un tirage passe par le pilote Windows — le pilote DNP
date de 2017 et n'a pas de successeur. Si l'envoi direct n'est pas possible sur un poste,
Studio repasse par le pilote et l'écrit dans son journal.

### On voit ce qu'il y a dans un dossier avant de l'ouvrir

Au moment de choisir les photos, chaque sous-dossier affiche **combien d'images il contient**
et une vignette. Les dossiers sont rangés du **plus récent au plus ancien** — ce qui vient
d'arriver est en tête. Fini le dossier vide qu'on ouvre pour rien.

### Le client envoie ses photos depuis son téléphone

Un QR code à l'écran, le téléphone dépose les fichiers. Pas de câble, pas de carte, pas de
« vous avez un lecteur ? ».

### On rend les photos au client par un lien

**Envoyer au client** dépose la commande sur Dropbox et rend un lien, à donner de vive voix
ou par courriel. Les fichiers s'effacent d'eux-mêmes au bout de quelques jours.

### L'état des machines est lu, pas supposé

Un bandeau permanent : état, rouleau, encres, bac de maintenance, tirages restants **par
format**. Quand la machine est en cause, la tuile dit **quoi toucher** — pas seulement
« intervention nécessaire ».

### Rien ne se réimprime jamais tout seul

Si une impression n'a pas confirmé sa sortie — coupure, machine à l'arrêt, application fermée
en pleine commande — Studio le **signale** au démarrage suivant et **ne renvoie rien**. La
décision se prend depuis *Commandes du jour*, là où l'on voit ce qu'on renvoie.

> Cette règle n'est pas de la prudence de principe : une réimpression automatique a déjà
> sorti **58 tirages en double** sur une file déjà à l'arrêt.

### Les photos du client sont recopiées tout de suite

Dès la création de la commande. Le client reprend sa carte immédiatement, et l'historique ne
dépend plus d'un support qu'il a remporté.

### Les commandes des bornes restent lisibles

Si vous avez des bornes DiLand, Studio lit leurs commandes **sans les lui prendre** : il
travaille sur une copie, DiLand garde les siennes. Produits, quantités et **recadrages faits
par le client** sont repris tels quels.

Les photos sont archivées **de notre côté** et gardées trente jours — DiLand purge ses
dossiers sans prévenir.

### Photos d'identité

Détection du visage, cadrage aux cotes réglementaires selon le document choisi, et un
**gabarit de contrôle** qui montre si la tête est à la bonne hauteur et à la bonne taille
avant d'imprimer la planche.

### Le reste, en vrac

- **Mettre une commande de côté** pour servir quelqu'un d'autre, et la reprendre.
- **Formats libres** et agrandissements sur mesure, sans toucher au catalogue.
- **Corriger toute une sélection d'un coup** — exposition, contraste, température, N&B, yeux
  rouges.
- **Contour de découpe** imprimé sur le bord quand le tirage sort avec des marges.
- **Statistiques** de ce qui a été vendu, sur la période qu'on choisit.
- **Envoyer un rapport** : les journaux partent à l'adresse configurée, en un bouton. Rien ne
  part jamais tout seul — le poste travaille sur des photos de clients.

---

## 3. Deux choses à savoir avant de commencer

**C'est le catalogue qui décide de la machine**, pas l'opérateur. Un produit porte son
format, son prix, sa finition et sa destination : on choisit un produit, pas une imprimante.

**Les réglages sont propres à chaque poste.** Le vôtre n'a aucune raison de ressembler à
celui d'à côté : imprimantes, dossiers favoris, supports masqués, tout est local et rien
n'est imposé par le dépôt.

---

## En cas de pépin

| Ce qui arrive | Quoi faire |
|---|---|
| Une machine n'apparaît pas | Vérifier qu'elle est allumée, puis que DiLand est installé (il porte les SDK). Une machine en veille se réveille au premier tirage. |
| Un tirage n'est pas sorti | Le bandeau dit presque toujours pourquoi. La reprise se fait depuis *Commandes du jour*. |
| L'application ne démarre pas | Le message nomme le dossier qu'elle n'a pas pu créer : vérifier les droits, ou définir `STUDIO_DATA`. |
| Autre chose | *Paramètres* → **Envoyer un rapport**. |
