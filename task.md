# Exécution — 19/08/2026 : l'historique de 30 jours de Studio Photo Identité

Suite de `implementation_plan.md`. Photos **faites** (imprimées ou envoyées), travail gardé
en entier, détourage gardé sur le disque, bouton dans Studio Photo Identité seulement.

**1810 essais verts sur 1813** (39 neufs). Les 3 restants sont `De100BridgeIntegrationTests`,
qui démarrent leur propre relais sur un tube à instance unique déjà tenu par le
`Studio.De100Host.exe` du comptoir — défaut d'environnement connu, pas de code. Point de
départ : `main` à `becdf87` (1.5.36).

⚠ Tout a été compilé et essayé en **Release** : le Studio complet tourne en permanence sur ce
poste et tient `bin\Debug`. Il n'a pas été fermé — c'est l'application du comptoir.

---

## A. Ce que la lecture du code a donné, et qui a évité d'écrire un journal de plus

- [x] A1 **`PhotoIdentiteEnAttente` garde DÉJÀ tout ce que l'exploitant demande** : cadrage,
      repères crâne/menton/tête, axe du visage, redressement, N&B, fond blanc, fond gris,
      corrections fines, photos par planche, quantité. Rien à inventer côté données.
- [x] A2 **`IdPhotoView(TravailEnAttente)` sait déjà rouvrir une planche** sur l'écran de
      travail complet. C'est ce qui satisfait « qu'elles soient toujours capables d'envoyer
      par mail » sans écrire une seule ligne d'envoi : on rouvre SUR l'écran qui a le bouton.
- [x] A3 **Les commandes ne pouvaient pas servir de source** : elles ne gardent pas les
      repères (`TraduireIdentite` le documente), et une photo envoyée par courriel n'y est pas
      reconnaissable comme identité — elle n'aurait jamais paru dans l'historique.

## B. ⚠ Un défaut trouvé en chemin, et corrigé

- [x] B1 **`ConstruireLAttente` n'écrivait PAS `FondGris`.** `AppliquerLAttente` le lisait
      (avec un avertissement en trois lignes disant que c'était la troisième fois que ce champ
      manquait quelque part) — mais personne ne l'écrivait. Une planche mise de côté en fond
      gris revenait donc avec le fond du studio, sans rien d'anormal à l'écran ni au journal.
      **Quatrième fois pour ce champ-là.**
- [x] B2 La cause de fond est traitée : les deux sorties passent désormais par UNE méthode,
      `LaPlanche(photos, chemins, photoCourante)` — mise de côté et historique lisent la même.
      Un champ ajouté à `StripItem` se pose maintenant à un seul endroit.

## C. Le journal (incrément 1)

- [x] C1 `Studio.Store/HistoriqueIdentite.cs` : `PhotoFaite` (clé, premier geste, dernier
      geste, nom du fichier, chemin de la copie locale, 🖨/✉, n° de commande, résumé, et le
      `TravailEnAttente` complet) + le magasin — un fichier par photo, rétention 30 jours,
      purge à la lecture, écriture atomique, fichier abîmé ignoré.
- [x] C2 **Le nom du fichier est DÉDUIT de la clé** (SHA-256 tronqué) : retrouver une entrée
      est une lecture, pas un parcours de dossier. Avec un identifiant tiré au sort, chaque
      geste aurait relu tout le dossier.
- [x] C3 `Noter` **fusionne** avec l'entrée du jour : drapeaux et n° de commande repris,
      travail remplacé par le dernier. Imprimée puis envoyée = une tuile, deux pastilles.
- [x] C4 8 essais.

## D. L'alimentation (incrément 2)

- [x] D1 `AppServices.HistoriqueIdentite` → `identite\historique\`.
- [x] D2 `TirageIdentite.LancerAsync(…, surCommande)` — appelé la commande créée, exceptions
      avalées et journalisées : le papier est déjà parti, rien ne doit l'arrêter.
- [x] D3 `MailSendView(…, surEnvoi)` — appelé après `Facturer`, donc après un envoi réussi.
- [x] D4 `IdPhotoView.NoterDansLHistorique` : une entrée par photo retenue, avec le chemin de
      la **copie locale** et une planche à UNE photo (rouvrir une tuile ne doit pas ramener
      les quatre-vingts autres de la carte).
- [x] D5 `OnSendByMail` dépose le travail et met la photo à l'abri AVANT de partir : sans le
      dépôt, l'historique garderait le cadrage d'avant la dernière retouche.

## E. L'écran (incrément 3)

- [x] E1 `IdHistoriqueView` : planche virtualisée, vignettes par tranches en parallèle
      (même mécanique que le choix des photos), tuile = vignette + quand + norme + pastille.
- [x] E2 Une tuile touchée rouvre la photo **sur l'écran de travail**, avec un `Id` NEUF —
      l'entrée ne désigne aucune planche mise de côté, la rouvrir ne doit rien effacer.
- [x] E3 Fichier disparu : la tuile le dit (grisée, « fichier effacé ») et ne s'ouvre pas.
- [x] E4 Bouton « 🕘 Photos récentes · 30 j » sous « 📂 Ouvrir des photos », **masqué hors de
      Studio Photo Identité**. La condition est `Mode.IsIdentite` (le LOGICIEL) et non
      `EnIdentiteVerrouille` (la session) : sortir par le PIN ne doit pas faire disparaître le
      bouton sous les doigts de l'opérateur.

## F. Le détourage qui ne se refait pas (incrément 4)

- [x] F1 `MasqueSujet.Dossier` → `cache\masques\<méthode>\<empreinte>.png`. Lecture :
      mémoire → **disque** → réseau. Écriture dans les deux.
- [x] F2 **Un sous-dossier par méthode** (`couleur`, `birefnet-lite-fp16`, …) : changer de
      modèle dans les réglages change de dossier. Sans cela, le réglage passerait pour
      inopérant — défaut invisible et pénible à retrouver.
- [x] F3 `DejaEnMemoire` regarde le disque aussi : sinon l'écran annonçait six secondes
      d'attente pour aller lire un fichier en cinq millisecondes.
- [x] F4 Écriture atomique (fichier `.tmp` puis remplacement), signature PNG vérifiée à la
      lecture, fichier abîmé effacé, décodage protégé — les octets ne viennent plus forcément
      de nous.
- [x] F5 `Oublier()` vide aussi le disque ; `OublierLaMemoire()` reproduit un redémarrage.
- [x] F6 5 essais, dont « après un redémarrage, le masque revient du disque » et « le masque
      d'une méthode ne ressort pas pour une autre ».

## F bis. ⚠ Le piège qui rendait tout ce travail inutile

- [x] Fb1 **La clé du masque contenait le CHEMIN COMPLET**, et le nom de la copie venait de
      `chemin.GetHashCode()` — que .NET tire au sort à chaque démarrage du processus. Une
      photo rouverte depuis l'historique change de dossier (voir G3) : elle recevait donc une
      clé neuve, et **repayait son détourage en entier**. C'est-à-dire exactement ce que
      l'exploitant demande d'éviter.
- [x] Fb2 `Infrastructure/CopieDeTravail` : `Nom(nomDuClient, source)` et `Cle(chemin)`, tous
      deux faits du **nom + taille + date** — trois choses qu'une copie conserve (`File.Copy`
      garde la date d'écriture). On nomme la PHOTO, jamais l'endroit où elle est.
- [x] Fb3 Une copie déplacée d'une journée à l'autre **garde son nom** : elle a été nommée une
      fois pour toutes.
- [x] Fb4 Sorti de `IdPhotoView` pour être ESSAYABLE — la règle est trop fragile pour vivre
      dans un code-behind de 3 000 lignes que rien ne couvre. 7 essais.

## G. Le ménage (incrément 5)

- [x] G1 ⚠ **Rien ne purgeait `cache\travail`** : une copie par photo ouverte, sur quatre
      postes, depuis le 14/08. `Studio.Store/MenageDuCache.cs` : dossiers de copies datés de
      plus de 30 jours (la date vient du NOM du dossier, comme l'archivage des commandes), et
      masques de plus de 30 jours.
- [x] G2 `AppServices.MenageDuCacheEnFond()`, appelé par les DEUX applications —
      `RunMaintenanceInBackground` (Studio) et `FenetreIdentite` (Identité). L'entretien
      complet, lui, reste au Studio : archivage et sauvegarde sont des gestes sur les données
      de la boutique, qu'un second logiciel n'a pas à refaire.
- [x] G3 **Une copie de travail d'un autre jour est recopiée dans celui du jour**
      (`MettreALAbriAsync`). C'est ce qui aligne les deux rétentions : sans cela, une photo
      rouverte le 20ᵉ jour et réimprimée aurait laissé une fiche vivante jusqu'au 50ᵉ,
      pointant sur un fichier effacé au 30ᵉ.
- [x] G4 5 essais.

## H. ⚠ Le défaut qui vidait les planches rouvertes (20/08)

Noté la veille comme un « détail cosmétique » — la tuile suivante portait le nom de la COPIE
et non celui du client. **C'en était la trace visible, pas la cause.**

- [x] H1 `AppliquerLAttente` retrouve les photos **par leur nom de fichier**. La bande est
      remplie depuis `Identite.Chemins` : pour une entrée d'historique, ce sont les copies de
      travail, donc `IMG_1234-ab12cd34.jpg`. La fiche, elle, avait gardé `FileName = IMG_1234.jpg`.
      Aucune correspondance : **la boucle sautait toutes les photos**, et la planche rouverte
      revenait à neutre — cadrage, repères crâne/menton, fond blanc, corrections. Sans erreur
      à l'écran, sans une ligne au journal. C'est-à-dire toute la demande de l'exploitant.
- [x] H2 Une planche **mise de côté** est reprise sur le support du client, où les deux noms
      se confondent : elle marchait, et c'est pour cela que rien ne l'a signalé.
- [x] H3 `PhotoIdentiteEnAttente.NomSurLeDisque` (null quand il n'apprend rien), écrit par
      `LaPlanche` — l'unique sortie de B2, donc les deux chemins l'ont d'un coup.
- [x] H4 `Infrastructure/RepriseDeLaPlanche` : le nom à garder, et l'index à deux clés.
      **Le nom du client passe d'abord** — deux clients apportent souvent un `IMG_1234.jpg`,
      et reprendre le cadrage d'une inconnue sortirait le visage de quelqu'un d'autre.
      Sorti du code-behind pour être essayable, comme `CopieDeTravail` (Fb4). **14 essais.**
- [x] H5 `StripItem.Name` devient notifiant et la photo retrouvée est **renommée au nom du
      client** : la commande suivante et la tuile suivante ne portent plus celui d'une copie.
      C'est le « détail cosmétique » du 19/08, réglé par la même correction.

---

## Ce qui reste à faire

- [ ] **Le regarder tourner.** Rien de tout ceci n'a été vu à l'écran : le Studio complet
      tourne en permanence sur ce poste (comptoir) et tient `bin\Debug` — tout a été compilé
      et essayé en **Release**. À ouvrir sur Studio Photo Identité : imprimer une planche,
      rouvrir la tuile, vérifier que le fond blanc revient SANS attente.
- [ ] Publier (`identite-v1.5.37`) et poser sur les postes — ⚠ `tools\Publier.ps1` ne démarre
      plus sur ce PC, les étapes sont à refaire à la main.
- [ ] Décider si le Studio complet doit avoir le bouton lui aussi. Le journal, lui, est déjà
      alimenté par les deux applications.
