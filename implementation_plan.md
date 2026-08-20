# Plan d'implémentation — l'historique de 30 jours de Studio Photo Identité

Demande de l'exploitant, 19/08/2026. Le plan de la passe précédente reste dans `task.md`.

> « dans studio photo identité il faut implémenter l'historique de 30 jours sur toutes les
> photos faites, il faut également garder les modifications qui ont été faites, fond blanc
> correction etc, et qu'elles soient toujours capables d'envoyer par mail. Par ailleurs
> lorsqu'une photo a été détourée, il faut qu'elle garde ce détourage dans l'historique pour
> ne pas avoir à remettre le fond etc »

C'est le point **5** de la maquette validée le 14/08 : le bouton « 🕘 Photos récentes · 30 j »,
et la règle qui l'accompagne — *« les photos des 30 jours se rouvrent SUR CE MÊME ÉCRAN → tout
y est (réimprimer, e-mail, télécharger, changer pays/format), pas d'écran séparé aux fonctions
limitées »*.

---

## Ce qui existe déjà, et qu'il ne faut surtout pas réécrire

| Brique | Où | Ce qu'elle donne |
|---|---|---|
| `IdentiteEnAttente` / `PhotoIdentiteEnAttente` | `Studio.Store/TravailEnAttente.cs` | **La forme exacte du travail d'une planche** : norme du document, cadrage, repères crâne/menton/tête, axe du visage, redressement, N&B, fond blanc, fond gris, corrections fines, photos par planche, quantité |
| `AttenteStore` | idem | Un fichier JSON par entrée, rétention **30 jours**, purge à la lecture, écriture atomique, fichier abîmé ignoré |
| `IdPhotoView(TravailEnAttente)` | `Views/IdPhotoView.xaml.cs:230` | **Rouvre une planche telle qu'elle a été laissée**, sur l'écran de travail complet |
| `ConstruireLAttente()` / `AppliquerLAttente()` | idem, l. 2857 / 2933 | La traduction dans les deux sens, déjà éprouvée en boutique |
| `TravailDepuisCommande` | `Studio.Store` | Le précédent : traduire un enregistrement en travail reprenable, **avec un Guid NEUF** |
| `MettreALAbriAsync` | `IdPhotoView.xaml.cs:564` | La photo est déjà recopiée en local (`cache\travail\<jour>\`) à son ouverture |
| `MasqueSujet` | `Studio.Imaging` | Le détourage et sa mémoire — 4 masques, **en RAM seulement** |
| `ThumbnailService` | `Studio.Core` | Vignettes en cache, pour la grille de l'historique |

**Rien de la donnée n'est à inventer.** Tout ce que l'exploitant demande de garder est déjà
décrit par `PhotoIdentiteEnAttente`, à une exception près : le détourage lui-même.

---

## Les quatre décisions du plan

### 1. Un journal à part, `identite\historique\`, et non les commandes

Les commandes (`orders\`) gardent déjà les photos et les réglages 90 jours, et
`TravailDepuisCommande.TraduireIdentite` sait les rouvrir. **Ce n'est pourtant pas la bonne
source**, pour deux raisons écrites dans le code lui-même :

- ⚠ **les repères de crâne et de menton ne sont PAS dans la commande** (`TraduireIdentite` le
  documente) : une planche rouverte depuis une commande revient avec `Prete = false` et
  relance la détection de visage. Le placement manuel — celui qu'on rouvre justement pour ne
  pas le refaire — est perdu ;
- **une photo envoyée par courriel n'est pas reconnaissable comme identité** dans une
  commande : elle porte le produit « envoi courriel », sans taille de case. Elle
  n'apparaîtrait donc jamais dans l'historique, alors que l'exploitant demande *toutes* les
  photos faites.

Écrire les repères dans `OrderItem` reviendrait à changer l'enregistrement **comptable** pour
un confort d'écran. On fait donc un journal à part, qui a exactement la forme du travail :

```
D:\PhotoStudioData\identite\historique\<guid>.json     (un fichier par photo faite)
```

Contenu : un `TravailEnAttente` — le même objet, la même sérialisation, les mêmes essais —
augmenté de ce que l'historique seul demande : quand, ce qui a été fait (imprimée / envoyée /
les deux), le numéro de commande, et le nom du fichier du client. `HistoriqueIdentite` est le
jumeau d'`AttenteStore` : rétention 30 jours, purge à la lecture, un fichier par entrée.

Les commandes restent la vérité comptable ; le journal n'est qu'un index de travail. Il ne
facture rien et ne solde rien.

### 2. Une photo FAITE, c'est une photo imprimée ou envoyée — tranché le 19/08

> « il faudrait que la photo aille dans l'historique à partir du moment où elle a été
> envoyée par mail ou imprimée, et pas dès qu'elle a été ouverte »

Pas les photos simplement ouvertes : la carte d'un client en porte quatre-vingts, et
l'historique se remplirait de ce qu'on n'a fait que regarder.

- à l'**impression** : `TirageIdentite.LancerAsync` gagne un rappel `surCommande`, appelé une
  fois la commande CRÉÉE — le moment exact où le papier est engagé. L'écran y porte ses
  photos retenues (`Quantite > 0`), et lui seul le peut : il est le seul à tenir les repères
  de crâne et de menton ;
- à l'**envoi par courriel** : `MailSendView` gagne un rappel `surEnvoi`, appelé **après
  l'envoi réussi et sa facturation**. C'est la règle même de cet écran — « rien n'est facturé
  quand l'envoi échoue » : rien ne doit être historisé non plus ;
- **une entrée par photo et par journée** (clé : fichier + jour). Le client qui repart avec sa
  planche ET son courriel n'a fait faire qu'une photo : une tuile, deux pastilles 🖨 ✉. Le
  travail gardé est le DERNIER — l'opérateur a pu recadrer entre les deux —, l'heure gardée
  est celle du PREMIER geste.

### 3. Les pixels doivent survivre 30 jours, et le ménage doit suivre

L'entrée pointe sur la **copie locale** faite par `MettreALAbriAsync` (`cache\travail\<jour>\`) :
elle est déjà sous `DataRoot` et survit au retrait de la carte du client. Deux compléments :

- la mise à l'abri est **garantie** avant d'écrire l'entrée (si la copie a échoué, on retombe
  sur la copie du dossier de la commande, qui existe toujours) ;
- ⚠ **`cache\travail` n'est purgé par rien aujourd'hui** — il grossit indéfiniment sur les
  quatre postes. Il reçoit la même rétention de 30 jours que le journal : l'entrée et ses
  pixels disparaissent ensemble. C'est la règle déjà posée par `KioskOrderJournal.Purge` —
  *« ce sont des photos de clients, et une copie qu'on ne sait plus rattacher à personne n'a
  aucune raison de rester »*.

### 4. Le détourage se garde SUR LE DISQUE

C'est la partie neuve, et la demande explicite : *« qu'elle garde ce détourage dans
l'historique pour ne pas avoir à remettre le fond »*.

Aujourd'hui `MasqueSujet` garde **4 masques en RAM**, perdus à la fermeture de l'application.
Rouvrir dans trois jours une photo à fond blanc repaierait un passage complet du réseau —
5,9 s mesurés sur la Quadro P2000, davantage sur kodakidpc — et rejouerait le défaut connu du
second passage (mémoire « le fond qui se dégrade »).

`MasqueSujet` gagne donc un **étage disque** sous le cache du poste :

```
D:\PhotoStudioData\cache\masques\<modèle>\<empreinte>.png
```

- on lit : mémoire → disque → réseau ; on écrit dans les deux ;
- le masque gardé est le **masque nu**, à sa taille de calcul (petit côté 1024) : quelques
  dizaines de Ko, et c'est déjà celui que la mémoire garde ;
- ⚠ **le nom du modèle est dans le chemin.** `MasqueSujet.Oublier()` est appelé quand
  l'exploitant change de modèle de détourage dans les réglages ; un masque sur disque qui
  survivrait à ce changement ferait « changer de modèle ne change plus rien », défaut
  invisible et pénible à trouver. `Oublier()` vide donc aussi le dossier ;
- l'empreinte est celle qui existe déjà (`CleDuFichier` : chemin | taille | date d'écriture),
  passée au SHA-256 pour faire un nom de fichier ;
- même rétention de 30 jours que le reste du cache.

**Le bénéfice dépasse l'historique** : le récapitulatif, l'impression et le courriel ne
repaieront plus le réseau d'un lancement de l'application à l'autre.

---

## L'écran

Un bouton **« 🕘 Photos récentes · 30 j »** dans le bloc d'actions du panneau de droite de
`IdPhotoView.xaml`, à côté de « 📂 Ouvrir des photos » — les deux seules sorties de la page,
plus celle-ci, qui n'en est qu'une variante : *ouvrir une photo qu'on a déjà faite*.

`IdHistoriqueView` : une grille de vignettes, la plus récente d'abord, une tuile par photo —
vignette, jour et heure, norme visée, « 6 photos », et une pastille ✉ / 🖨 pour dire ce qui a
été fait. Un toucher rouvre la photo **sur l'écran de travail**, avec tout : cadrage, repères,
fond blanc déjà calculé, corrections, quantité — et donc les boutons Imprimer et Envoyer par
courriel, qui sont ceux de l'écran.

⚠ `IdPhotoView` est **partagé** avec le Studio complet, et le bouton ne doit s'y voir **que
dans Studio Photo Identité** — tranché le 19/08. Le Studio complet garde « Commandes du
jour » pour retrouver une planche.

La condition est `App.Services.Mode.IsIdentite`, et **pas** `AccueilStudio.EnIdentiteVerrouille` :
c'est le LOGICIEL qui répond à la question, pas la session. Sortir par le PIN pour dépanner
ne doit pas faire disparaître l'historique sous les doigts de l'opérateur.

Le journal, lui, est écrit par les deux applications — elles partagent la racine de données,
et une planche tirée dans l'une doit se retrouver dans l'autre.

---

## Les incréments, dans l'ordre — compilés et essayés un par un

1. **`HistoriqueIdentite`** (`Studio.Store`) : le journal, sa rétention, sa purge, sa
   traduction en travail reprenable (Guid neuf). Essais unitaires : écriture/relecture, purge
   à 30 jours, fichier abîmé ignoré, mise à jour d'une entrée existante.
2. **L'alimentation** : `TirageIdentite.LancerAsync` (planches retenues seulement) et
   `MailSendView` + `IdPhotoView.OnSendByMail` (sur envoi réussi). Essais : le lot à zéro
   n'entre pas, l'envoi échoué n'entre pas, deux gestes sur la même photo = une entrée.
3. **`IdHistoriqueView`** + le bouton + la réouverture. C'est là que l'exploitant regarde
   tourner.
4. **Le masque sur disque** dans `MasqueSujet` + le modèle dans le chemin + `Oublier()` qui
   vide le dossier. Essais : relecture après vidage de la mémoire, invalidation au changement
   de modèle.
5. **La purge de `cache\travail` et de `cache\masques`** à 30 jours, dans l'entretien du
   démarrage (`RunMaintenanceInBackground`, à côté de `Archiver.ArchiveOldOrders`).

Point de départ : `main` à `becdf87` (version 1.5.36). ⚠ L'arbre porte déjà trois fichiers
modifiés non validés sur la file d'impression (`RessourceDImpression`, `SuiviImpressions`,
`FileDImpressionTests`) — ils ne sont pas de cette passe et ne seront pas touchés.
