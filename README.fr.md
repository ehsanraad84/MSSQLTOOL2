# MSSQL Tool pour SSMS 22

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md)

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 **Plus de 100 téléchargements — merci de votre soutien !**

## Liens

Dépôt GitHub

https://github.com/PuLuShen/MSSQLTOOL

Dernière version (ZIP + VSIX)

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS Gallery (pour SQL Server Management Studio)

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery (pour Visual Studio)

https://www.vsixgallery.com/extension/MSSQLTool

Documentation (Wiki)

https://github.com/PuLuShen/MSSQLTOOL/wiki

## Installation

1. Téléchargez le ZIP de la dernière version ci-dessus et exécutez `安装或重装.cmd` (recommandé).
2. Ou installez le dernier VSIX depuis SSMS Gallery ci-dessus.
3. Les mises à jour sont intégrées : **Paramètres → Mises à jour** peut planifier « installer à la fermeture de SSMS ».

> **MSSQL Tool est une extension SSMS (SQL Server Management Studio) gratuite et open source pour SSMS 22** —
> un add-in pour SQL Server Management Studio qui apporte la complétion SQL contextuelle, le formatage T-SQL,
> l'historique des requêtes, la surveillance de l'état des serveurs et l'export des résultats vers Excel/Google Sheets/e-mail.
>
> Les utilisateurs de SSMS 22 (SQL Server Management Studio version 22) cherchent souvent des plugins gratuits pour
> remplacer SQL Prompt ; MSSQL Tool est fait exactement pour cela : une extension SSMS gratuite avec complétion SQL,
> formatage et traitement des résultats intégrés, destinée aux développeurs SQL Server et aux DBA qui veulent écrire
> leurs requêtes plus vite et traiter plus de résultats dans SQL Server Management Studio.

> MSSQL Tool est un fork de [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools) (Apache-2.0). Le moteur de complétion SQL, les options de formatage ainsi que les couches de paramètres et de localisation ont été refondus par-dessus.

## Points forts

- **Interface multilingue (8 langues : English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español)** — la plupart des extensions SSMS sont uniquement en anglais.
- **Alternative gratuite à SQL Prompt** pour la complétion SQL, le formatage T-SQL, l'historique des requêtes et l'export vers Excel/e-mail.
- **Basé sur Axial SQL Tools** (maintenu depuis 2016), avec un moteur de complétion et une couche de localisation améliorés.

*Remarque : l'export vers Google Sheets et la synchronisation de scripts GitHub sont expérimentaux et en cours de développement.*

## Captures d'écran

**Complétion SQL intelligente** — des suggestions contextuelles pendant la frappe : schémas, tables, vues, colonnes, procédures, fonctions et paramètres, chacun avec une icône de type, un filtre de catégorie et les détails de l'objet sélectionné.

<img src="pics/1.png" width="620" alt="Liste de complétion SQL pendant la saisie de SELECT * FROM">

**Paramètres** — chaque fonction possède ses propres options, enregistrées immédiatement. L'interface suit la langue de SSMS (chinois simplifié ici ; l'anglais et d'autres langues sont également disponibles).

<img src="pics/2.png" width="760" alt="Fenêtre de paramètres de MSSQL Tool avec l'onglet Complétion SQL">

**Menu Outils** — les commandes de l'extension, avec leur raccourci clavier lorsqu'il est configuré.

<img src="pics/3.png" width="520" alt="Commandes de MSSQL Tool dans le menu Outils de SSMS">

**Statistiques de jeu de résultats** — sélectionnez des cellules dans la grille de résultats pour voir le nombre, la somme, la moyenne, le minimum, le maximum et les valeurs distinctes ; le même panneau permet de localiser une colonne, de rechercher une valeur et de figer des colonnes.

<img src="pics/4.png" width="820" alt="Panneau de statistiques de MSSQL Tool à côté d'une grille de résultats">

## Fonctionnalités

### Édition SQL

- **Complétion SQL intelligente :** suggestions contextuelles pour les bases de données, schémas, tables, vues, colonnes, procédures, fonctions, paramètres, synonymes et objets de serveurs liés.
- **Une complétion qui suit vos alias :** `c.` ne liste que les colonnes de cette table et insère le nom de colonne seul, de sorte que l'alias saisi reste devant.
- **Prise en charge d'UPDATE :** `UPDATE t SET` propose les colonnes modifiables de la cible, y compris lorsque celle-ci est un alias déclaré plus bas dans la clause `FROM`.
- **Types de données et indications :** `INT`, `NVARCHAR(50)`, `DATETIME2` et les formes usuelles `WITH (NOLOCK)` / `OPTION (RECOMPILE)` sont proposés là où il faut.
- **Aide sur les fonctions dans la liste :** plus de 36 fonctions intégrées affichent leur signature, une explication en une ligne et un exemple à copier.
- **Mots-clés extensibles :** la liste intégrée peut être complétée depuis Paramètres → Complétion SQL (un mot-clé personnalisé par ligne).
- **Cache de métadonnées de complétion :** chargement en arrière-plan avec progression détaillée, effacement/actualisation manuels et approbation facultative du certificat SQL Server.
- **F12 Atteindre la définition :** ouvre les définitions d'objets de la base et navigue vers les définitions SQL locales.
- **Formatage T-SQL** (conservé pour les habitudes existantes ; SSMS 22 formate en natif — voir *Formatage* ci-dessous)
- **Modèles de requêtes :** édition en ligne avec Ctrl+S, favoris, **étiquettes** (`-- tags: 报表, 日结`) avec recherche par étiquette (`#tag`) et une liste des **10 plus récents** en haut du sélecteur.
- **Extraits de code**
- **Développer `SELECT *` en liste de colonnes**
- **Sélectionner l'instruction courante**
- **Basculer le commentaire de bloc**
- **Recherche rapide :** recherche d'objets multi-bases, avec cible reprise de l'Explorateur d'objets, filtres, génération de script et ouverture dans une nouvelle requête.
- **Script de définition d'objet**

### Outils de jeu de résultats

- **Panneau de statistiques (flottant) :** s'ouvre de lui-même lorsque vous cliquez dans les résultats et se ferme lorsque vous cliquez à l'extérieur. Sélectionnez des cellules et il affiche le **nombre, la somme, la moyenne, le minimum, le maximum et les valeurs distinctes** de la sélection, ainsi que le vrai nom de colonne ; il est redimensionnable, mémorise sa taille et sa position, et peut **figer les N premières colonnes**.
- **Recherche de colonne :** saisissez une partie d'un nom de colonne et les résultats défilent jusqu'à elle (`DDZ` ne se résout jamais en `DDZT`).
- **Fenêtre d'outils de jeu de résultats :** la même recherche plus des statistiques par colonne dans une boîte de dialogue ancrée (Outils → *Recherche et statistiques de jeu de résultats*), avec copie dans le presse-papiers.

### Traitement des résultats et transfert de données

- **Exporter la grille vers Excel**
- **Envoyer les résultats de la grille par e-mail**
- **Exporter la grille en table temporaire**
- **Copier les noms de colonnes et les valeurs en INSERT, CSV, JSON, XML ou HTML**
- **Transfert de données en masse**

### Surveillance et flux de travail

- **Historique des requêtes**
- **Tableau de bord de l'état des serveurs**
- **Résumé des statistiques d'exécution**
- **Avertissements sur les transactions et Always Encrypted**
- **Couleurs de connexion par environnement serveur et base de données**
- **Temps d'exécution des requêtes en millisecondes**
- **Valeurs numériques alignées à droite dans la grille**
- **Informations sur la version de SQL Server**
- **Dossier de données et de paramètres configurable** (autre disque, dossier synchronisé ou périphérique portable)

## Fonctionnalités expérimentales

Elles fonctionnent, mais leur conception évolue encore : attendez-vous à des changements entre les versions et revérifiez les paramètres concernés après une mise à jour.

| Fonctionnalité | État |
| --- | --- |
| **Export vers Google Sheets** | Bêta — la configuration OAuth et le mappage des colonnes changent encore. |
| **Synchronisation de scripts GitHub** | Bêta — envoi et récupération de fichiers entiers, résolution des conflits manuelle. |

## Formatage

SSMS 22 embarque son propre formateur T-SQL, donc **la fonction de formatage de cette extension n'est plus développée**. La commande fonctionne toujours et est conservée pour les habitudes existantes — elle se trouve désormais dans le menu **Outils** (Ctrl+Shift+Alt+F) — mais aucune nouvelle option de formatage ne sera ajoutée.

## Soutien

<img src="pics/赞赏码.png" width="320" alt="Code QR de don" />

## FAQ

**Qu'est-ce que MSSQL Tool ?**
Une extension SSMS 22 gratuite pour la complétion SQL, le formatage et le traitement des résultats.

**MSSQL Tool est-il gratuit ?**
Oui, il est open source sous Apache-2.0.

**Comment se compare-t-il à SQL Prompt ou dbForge ?**
MSSQL Tool est gratuit et open source, avec synchronisation GitHub et export vers Google Sheets intégrés. SQL Prompt et dbForge sont des alternatives commerciales.
