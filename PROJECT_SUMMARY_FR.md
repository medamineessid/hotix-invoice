# HOTIX Invoice Extractor

Système local Windows d'extraction de factures avec un backend Python OCR, un client WPF et un installateur Inno Setup.

## Vue D'ensemble

Le dépôt est organisé en trois couches opérationnelles :

1. L'installateur prépare la machine, vérifie l'environnement et publie l'application.
2. Le serveur Python effectue l'ingestion, l'OCR, l'extraction des champs et le repli Gemini.
3. Le client C# WPF lance le serveur, fournit l'interface utilisateur, gère la sélection des fichiers et affiche les résultats.

Le projet est conçu pour une exécution locale sous Windows. Le client et l'installateur supposent un environnement Windows de bureau, et le pipeline OCR dépend de Python, PaddleOCR et Poppler.

## Structure du Dépôt

- `server/` contient le service FastAPI et la logique d'extraction.
- `client/` contient l'application WPF, les ViewModels, les converters et le code-behind des fenêtres.
- `installer/` contient le script Inno Setup et la documentation associée.
- `scripts/` contient l'automatisation de l'installation et du démarrage.
- `README.md` est le guide utilisateur, mais le code et l'installateur font foi.

## Architecture

Le flux d'exécution est le suivant :

1. Installer ou valider Python, Poppler et les prérequis .NET.
2. Publier le client C#.
3. Démarrer l'application WPF.
4. L'application lance localement le serveur Python.
5. L'application attend `/health` avant d'afficher l'interface principale.
6. Si Gemini n'est pas configuré, l'assistant de première exécution s'affiche.
7. L'utilisateur sélectionne des fichiers ou dossiers et lance l'extraction.
8. Le serveur renvoie des champs de facture normalisés et des scores de confiance.
9. Le client affiche les résultats, les éléments incomplets, les aperçus et les actions d'export.

## Serveur Python

### [server/main.py](server/main.py)

C'est le point d'entrée FastAPI.

Responsabilités :

- créer le moteur OCR au démarrage du cycle de vie de l'application,
- fournir `/health` et `/engine-status`,
- accepter les fichiers uploadés sur `/extract`,
- essayer Gemini en premier lorsqu'il est demandé ou configuré,
- revenir à l'OCR local si Gemini est indisponible ou désactivé,
- normaliser les exceptions en réponses HTTP.

Fonctions importantes :

- `lifespan` : initialise et ferme le moteur OCR.
- `health` : renvoie un simple statut OK.
- `engine_status` : indique si Gemini est configuré et joignable, et si l'OCR est disponible.
- `_extract_first_page_bytes` : convertit une page en octets PNG pour Gemini.
- `_run_gemini_extraction` : invoque Gemini et transforme un JSON réussi en modèle API.
- `_run_ocr_extraction` : exécute PaddleOCR sur toutes les pages et assemble le résultat.
- `extract` : valide les types d'entrée, charge les images de facture et route vers Gemini ou l'OCR.

Changements importants effectués :

- Les imports ont été rendus relatifs au package afin que `uvicorn server.main:app` fonctionne depuis la racine du dépôt.
- Gemini a été migré de l'API `google.generativeai` obsolète vers `google.genai`.
- Le modèle codé en dur `gemini-1.5-flash` a été remplacé par `gemini-3.5-flash`.
- CORS a été resserré, passant d'origines et de méthodes génériques à des origines localhost et aux méthodes GET/POST.
- Le champ `engine_used: Literal["gemini", "ocr"]` a été ajouté à `InvoiceExtractionResponse` et défini dans les deux chemins de retour (Gemini et OCR), permettant au client de savoir quel moteur a produit chaque résultat.

### [server/models.py](server/models.py)

Définit le schéma de l'API.

Modèles :

- `InvoiceExtractionResponse` : champs de facture, confiance et texte OCR brut.
- `HealthResponse` : payload de santé.
- `ErrorResponse` : payload d'erreur structurée.

### [server/ingestion.py](server/ingestion.py)

Convertit les fichiers uploadés en pages image.

Fonction importante :

- `load_invoice_images` : gère les PDF via Poppler/pdf2image et les fichiers image via Pillow.

Changement important :

- `.bmp` a été ajouté aux extensions prises en charge pour que le backend corresponde au README et aux filtres client.

### [server/ocr_engine.py](server/ocr_engine.py)

Encapsule PaddleOCR dans une abstraction plus petite et adaptée à l'application.

Types et méthodes importants :

- `OCRResult` : sortie normalisée pour une page.
- `OcrEngineError` : wrapper d'erreur runtime OCR.
- `PaddleOcrEngine.__init__` : stocke la langue et diffère le chargement du modèle.
- `PaddleOcrEngine.ocr` : charge PaddleOCR à la demande.
- `PaddleOcrEngine.recognize` : exécute l'OCR sur une image PIL.
- `PaddleOcrEngine._normalize_result` : convertit les résultats OCR bruts en lignes ordonnées.
- `PaddleOcrEngine._iter_detections` : normalise les formes de sortie de PaddleOCR.
- `PaddleOcrEngine._parse_detection` : convertit une détection en ligne OCR.
- `PaddleOcrEngine._extract_text_and_confidence` : extrait texte et confiance à partir du payload.

Changements importants :

- Les boîtes englobantes malformées ne sont plus ignorées silencieusement ; elles génèrent maintenant un message de debug.
- Les paramètres `show_log=False` et `use_angle_cls=True` ont été supprimés du constructeur `PaddleOCR()`, et `cls=True` de l'appel `ocr()` — ces paramètres ont été retirés dans PaddleOCR 3.x et causaient des échecs d'exécution.

### [server/field_extractor.py](server/field_extractor.py)

Implémente le moteur heuristique de résolution des champs.

Données principales :

- `FIELD_ORDER` : ordre canonique des champs facture.
- `FIELD_ALIASES` : variantes d'étiquettes pour les champs.
- `NUMERIC_FIELDS` : champs monétaires.
- `TEXT_FIELDS` : champs textuels.
- `FieldSelection` : meilleur candidat retenu pour un champ.

Fonctions principales :

- `extract_invoice_fields` : renvoie les champs normalisés.
- `extract_field_confidences` : renvoie la confiance par champ.
- `extract_raw_text` : renvoie le texte OCR en ordre de lecture.
- `compute_confidence` : moyenne les confiances des champs renseignés.
- `_extract_field_selections` : orchestre la sélection pour tous les champs.
- `_select_best_selection_for_field` : évalue tous les ancres pour un champ.
- `_selection_from_same_line` : gère les paires étiquette/valeur sur la même ligne.
- `_selection_from_next_line` : gère les valeurs sur la ligne suivante.
- `_contains_any_alias` : vérifie les alias normalisés.
- `_extract_inline_value` : extrait une valeur sur la même ligne que l'étiquette.
- `_find_next_candidate` : trouve la ligne plausible suivante la plus proche.
- `_clean_candidate_value` : normalise les valeurs candidates selon le type de champ.
- `_score_candidate` : note un candidat selon la géométrie et la proximité avec l'étiquette.
- `_is_plausible_field_value` : rejette les valeurs manifestement invalides.
- `_nearest_amount_line` : localise une ligne de montant voisine.
- `_normalize_supplier_or_client` : nettoie les noms d'entité.
- `_score_field_candidate` : helper de scoring spécifique au champ.
- `_has_numeric_content` : vérifie si une chaîne ressemble à une valeur numérique.
- `_is_label_only_line` : détecte les lignes qui ne contiennent qu'une étiquette.
- `_select_preferred_amount_candidate` : départage plusieurs candidats monétaires.
- `_extract_field_from_block` : extraction de secours par bloc.
- `_find_best_block` : sélectionne le meilleur bloc OCR pour un champ.
- `_group_lines_by_page` : regroupe les lignes OCR par page.
- `_fallback_field_search` : recherche heuristique de dernier recours.
- `_rank_candidate_lines` : classe les lignes candidates lorsque la structure est faible.

Note de conception :

L'extracteur utilise des heuristiques en couches plutôt qu'un simple regex. C'est nécessaire parce que l'OCR de facture est bruité, les étiquettes varient fortement, et beaucoup de documents placent les valeurs soit sur la même ligne que l'étiquette, soit sur la ligne suivante.

### [server/gemini_extractor.py](server/gemini_extractor.py)

Gère l'extraction via Gemini.

Fonctions importantes :

- `GeminiExtractionError` : type d'échec spécifique au domaine.
- `load_gemini_api_key` : lit la clé API depuis l'environnement ou appsettings.json.
- `extract_with_gemini` : envoie l'image de facture à Gemini et analyse la réponse JSON.

Changements importants :

- `google.generativeai` a été remplacé par `google.genai`.
- `gemini-1.5-flash` a été remplacé par `gemini-3.5-flash`.
- L'import `base64` a été supprimé car il était inutilisé.
- Le traitement des erreurs a été conservé en français afin de préserver les messages UX existants.

### [server/utils.py](server/utils.py)

Utilitaires partagés pour la géométrie et le texte.

Types importants :

- `BoundingBox` : helper géométrique utilisé pour le scoring OCR.
- `OCRLine` : une ligne OCR avec texte, géométrie, confiance et métadonnées de page.

Fonctions importantes conservées :

- `normalize_text`
- `collapse_text`
- `looks_like_latin_text`
- `normalize_text_for_output`
- `extract_amount`
- `clean_amount`
- `extract_date`
- `clean_date`

Helpers inutilisés supprimés :

- `contains_keyword`
- `clean_invoice_number`
- `normalize_supplier_client`
- `sort_by_reading_order`

Raison :

Ces helpers n'avaient aucun appelant et n'ajoutaient que du bruit de maintenance.

### [server/verify_system.py](server/verify_system.py)

Ajouté parce que `setup.ps1` attendait une étape de vérification qui n'existait pas.

Vérifications effectuées :

- importe `paddleocr`,
- vérifie que `google.genai` ou `google.generativeai` est installé,
- vérifie que `pdfinfo` est dans le PATH,
- exécute `pdfinfo -v`,
- retourne un code d'échec avec un message clair si une vérification échoue.

## Client C#

### [client/App.xaml.cs](client/App.xaml.cs)

Bootstrapper de l'application.

Méthodes importantes :

- `OnStartup` : résout les chemins, démarre le serveur OCR, attend la santé, affiche le splash, gère l'assistant de première exécution et ouvre la fenêtre principale.
- `GetStatusMessage` : mappe le temps écoulé vers un texte visible pour le splash.
- `FindFile` : helper générique de découverte de chemin.
- `CleanupServer` : arrête le processus Python.
- `HandleGlobalException` : attrape les exceptions non gérées et les signale.
- `IsFirstRun` : vérifie si Gemini est configuré.

Changement important :

- Le chemin appsettings du serveur est maintenant lu via le résolveur partagé de `MainViewModel`.

### [client/MainWindow.xaml.cs](client/MainWindow.xaml.cs)

Code-behind de la fenêtre principale.

Méthodes importantes :

- `MainWindow` : initialise la fenêtre et attache les événements de cycle de vie.
- `OnLoaded` : initialise le ViewModel partagé.
- `OnClosing` : libère le ViewModel partagé.
- `TitleBar_MouseLeftButtonDown` : gère le déplacement et le maximisé.
- `MinimizeButton_Click` : réduit la fenêtre.
- `MaximizeButton_Click` : bascule l'état maximisé.
- `CloseButton_Click` : ferme la fenêtre.
- `Window_DragOver` : gère l'aperçu drag/drop.
- `Window_Drop` : envoie les dossiers déposés au ViewModel.
- `AddButton_Click` : ouvre le menu contextuel d'ajout.

Changement important :

- La fenêtre ne crée plus son propre `MainViewModel`. Elle utilise l'instance unique créée dans `App.xaml.cs`.

### [client/MainWindow.xaml](client/MainWindow.xaml)

Disposition et styles de l'interface principale.

Zones principales :

- barre de titre personnalisée,
- panneau de contrôle,
- panneau de liste de fichiers,
- bannière de synthèse,
- onglets de résultats,
- panneau d'aperçu brut,
- barre d'état,
- overlay d'erreur serveur.

Changements importants :

- Le panneau Gemini inline a été supprimé.
- Le bouton de fermeture de l'aperçu utilise désormais uniquement la commande.
- Le bouton engrenage ouvre maintenant la boîte de dialogue Gemini.
- Les badges de confiance continuent d'utiliser le converter à trois niveaux.
- Un badge `"Local (hors ligne)"` apparaît maintenant à côté de l'indicateur de confiance pour les lignes extraites par OCR local, piloté par la liaison `IsLocalOcr`. Les lignes Gemini n'affichent aucun badge.

### [client/ViewModels/MainViewModel.cs](client/ViewModels/MainViewModel.cs)

Le fichier client le plus important.

Responsabilités :

- stocker l'état de sélection des fichiers et dossiers,
- gérer les commandes d'extraction,
- suivre la progression et l'état du serveur,
- interroger l'état du moteur,
- persister les réglages Gemini,
- persister le dernier dossier utilisé,
- gérer l'aperçu de la ligne sélectionnée,
- exporter les résultats,
- prendre en charge les relances et les bascules de sélection.

Commandes importantes :

- `BrowseFolderCommand`
- `BrowseFilesCommand`
- `StartExtractionCommand`
- `CancelExtractionCommand`
- `ExportExcelCommand`
- `ClearCommand`
- `RerunCommand`
- `RerunAllErrorsCommand`
- `ToggleAllFilesCommand`
- `ToggleAllRowsCommand`
- `OpenSavedFolderCommand`
- `ToggleSettingsCommand`
- `SaveGeminiKeyCommand`
- `ClearSelectedRowCommand`

Méthodes importantes :

- `InitializeAsync`
- `CheckEngineStatusAsync`
- `SaveGeminiKeyAsync`
- `LoadGeminiKeyFromAppSettings`
- `BrowseFolder`
- `BrowseFiles`
- `SetFolderFromDrop`
- `LoadSettings`
- `SaveSettings`
- `Dispose`

Changements importants :

- Le ViewModel est devenu l'unique source de vérité pour le cycle de vie de l'application.
- Il expose maintenant le résolveur de chemin partagé pour le stockage Gemini.
- `ToggleSettingsCommand` n'ouvre plus un panneau inline ; il ouvre la boîte de dialogue Gemini.
- `ClearSelectedRowCommand` a été ajouté pour soutenir proprement le bouton de fermeture de l'aperçu.
- La logique de chemin appsettings est centralisée afin qu'`App.xaml.cs` et l'assistant utilisent exactement la même résolution.
- Un `DispatcherTimer` (pas `System.Threading.Timer`) interroge `/engine-status` toutes les 45 secondes sur le thread UI, afin que `GeminiAvailable` reflète l'état actuel sans risque d'`InvalidOperationException` due à un changement de propriété inter-thread.

### [client/ViewModels/InvoiceRowViewModel.cs](client/ViewModels/InvoiceRowViewModel.cs)

Modèle de présentation pour une ligne de facture.

Méthodes importantes :

- `FromSuccess` : convertit un `InvoiceResult` réussi vers le ViewModel.
- `FromError` : crée une ligne pour une extraction échouée.
- `ToInvoiceResult` : supprimé car inutilisé.
- `SetField` : helper de propriété qui lève les notifications.
- `OnDerivedFieldChanges` : met à jour les propriétés dépendantes.

Propriétés importantes :

- affichage du fichier,
- champs extraits,
- drapeaux de champs manquants,
- affichage de confiance,
- texte de tooltip,
- état de sélection,
- état d'erreur,
- `EngineUsed` ("gemini" ou "ocr") et `IsLocalOcr` calculé pour les liaisons XAML.

### [client/InvoiceClient.cs](client/InvoiceClient.cs)

Wrapper HTTP utilisé par le ViewModel.

Méthode importante :

- `ExtractAsync` : upload un fichier vers `/extract` via multipart form data et renvoie un `InvoiceResult` parsé.

Changement important :

- `.bmp` est maintenant mappé vers `image/bmp`.

### [client/GeminiSetupWindow.xaml](client/GeminiSetupWindow.xaml)

Boîte de dialogue popup utilisée pour la configuration Gemini.

Elle contient :

- un texte explicatif,
- un PasswordBox pour la clé,
- un lien d'aide,
- des boutons ignorer et enregistrer,
- un label de message pour l'état.

### [client/GeminiSetupWindow.xaml.cs](client/GeminiSetupWindow.xaml.cs)

Gère le comportement de la popup.

Méthodes importantes :

- `GetApiKey_Click` : ouvre la page de création de clé Google AI Studio.
- `Ignore_Click` : ferme la boîte de dialogue.
- `Save_Click` : envoie la clé à la commande de sauvegarde du ViewModel.

Changement important :

- La logique manuelle d'écriture dans appsettings a été supprimée afin que la boîte de dialogue réutilise un seul chemin de sauvegarde.

### [client/Converters](client/Converters)

Petits converters UI utilisés dans toute la fenêtre.

Le plus important :

- `ConfidenceToColorConverter` : définit trois niveaux de badge, vert au-dessus de 0.75, orange de 0.40 à 0.75, et rouge en dessous de 0.40.

### [client/SplashScreen.xaml](client/SplashScreen.xaml)

UI de splash affichée pendant le démarrage du serveur.

### [client/ExcelWriter.cs](client/ExcelWriter.cs)

Helper d'export Excel pour les résultats.

Changement important :

- La colonne `"Moteur"` (position 11) a été ajoutée avec les libellés français `"Gemini (cloud)"` ou `"OCR local"` à partir de la propriété `EngineUsed`.

### [client/InvoiceResult.cs](client/InvoiceResult.cs)

Modèle côté client qui correspond à la réponse d'extraction du serveur.

Changement important :

- La propriété `EngineUsed` avec `[JsonPropertyName("engine_used")]` a été ajoutée pour désérialiser le nouveau champ de transparence du moteur envoyé par le serveur.

## Installateur

### [installer/Hotix.iss](installer/Hotix.iss)

Le script d'installation gère la préparation de la machine et le flux d'installation.

Responsabilités principales :

- détecter Python via le PATH et le registre,
- vérifier la version de Python,
- vérifier la connectivité internet,
- vérifier l'espace disque,
- valider la présence de `requirements.txt`,
- installer Python si nécessaire,
- créer le virtual environment,
- installer les dépendances Python,
- faire un rollback en cas d'échec,
- lancer l'application après installation.

Changements importants :

- La barrière minimale Python est revenue à 3.8+.
- Le seuil disque est maintenant de 2200 MB avec une arithmétique explicite.
- Les retries `pip` inspectent maintenant `stderr` pour distinguer les échecs permanents des échecs transitoires.
- `WizardForm.StatusLabel` est utilisé pour afficher les étapes visibles.
- `InternetGetConnectedState` est déclaré depuis `wininet.dll` et encapsulé dans un helper.

### [installer/InternetGetConnectedStateTest.iss](installer/InternetGetConnectedStateTest.iss)

Script de test minimal pour l'API de connectivité WinINet.

## Scripts

### [scripts/setup.ps1](scripts/setup.ps1)

Automatisation de l'installation.

Il :

- vérifie Python,
- vérifie Poppler,
- vérifie le .NET Desktop Runtime,
- crée le venv,
- installe les paquets Python,
- exécute le script de vérification,
- publie le client.

Changement important :

- le script de vérification attendu existe maintenant sous `server/verify_system.py`.

### [scripts/start.ps1](scripts/start.ps1)

Chemin de lancement recommandé.

Il :

- démarre le serveur OCR,
- attend `/health`,
- lance le client WPF publié,
- arrête le serveur lorsque le client se ferme.

### [scripts/start.bat](scripts/start.bat)

Lanceur batch pour les utilisateurs qui veulent un point d'entrée simple.

Changement important :

- il pointe maintenant vers `client/publish/Hotix.InvoiceClient.exe` afin de correspondre à la sortie publiée.

## Fichiers Supprimés ou Simplifiés

Les éléments suivants ont été supprimés car inutilisés ou trompeurs :

- les helpers serveur inutilisés dans `utils.py`,
- `ToInvoiceResult()` inutilisé dans `InvoiceRowViewModel`,
- le panneau Gemini inline dupliqué dans `MainWindow.xaml`,
- l'instance dupliquée de `MainViewModel` dans `MainWindow.xaml.cs`,
- l'ancien SDK Gemini obsolète,
- les artefacts `publish` suivis par git.

## Ce Qui a Mal Fonctionné

Plusieurs points étaient initialement incohérents :

- les imports serveur étaient écrits comme si les modules étaient de premier niveau,
- l'application créait plus d'une instance de ViewModel,
- Gemini utilisait un SDK déprécié et un modèle obsolète,
- l'installateur référençait un script de vérification absent,
- l'installateur annonçait un seuil disque mal justifié,
- les chemins documentés et les chemins réels de publication ne correspondaient pas,
- certains helpers et méthodes de conversion de modèles n'étaient plus utilisés,
- des binaires compilés étaient suivis dans git.

Chaque problème a dû être corrigé plutôt que contourné, car il touchait au démarrage, à l'exactitude ou à la maintenabilité.

## État Actuel

La base de code possède maintenant :

- un chemin de démarrage du serveur Python fonctionnel,
- un seul ViewModel partagé côté client,
- une intégration Gemini sur `google.genai`,
- la prise en charge du BMP dans toute la pile,
- un vrai script de vérification système,
- une logique d'installateur au moins cohérente en interne,
- des scripts de lancement alignés avec la vraie sortie publiée,
- une transparence du moteur d'extraction avec champ `engine_used` dans la réponse API, badge "Local (hors ligne)" dans l'UI et colonne "Moteur" dans l'export Excel,
- une interrogation automatique de l'état Gemini toutes les 45 secondes via `DispatcherTimer` (sûr pour le thread UI),
- compatibilité PaddleOCR 3.7.0 après suppression des paramètres obsolètes `show_log`, `use_angle_cls` et `cls`,
- version `paddlepaddle==3.2.0` conforme aux recommandations officielles de PaddleOCR.

## Notes de Maintenance

Les invariants les plus importants à préserver sont :

1. `server.main` doit rester importable comme module de package.
2. Le client doit conserver une seule instance de ViewModel pour toute la durée de vie de l'application.
3. La sauvegarde de la clé Gemini doit passer par un seul chemin.
4. La logique de l'installateur doit rester explicite sur le disque, le réseau et la version de Python.
5. Les chemins de sortie publiés dans les scripts et la documentation doivent rester identiques à la vraie sortie de build.
