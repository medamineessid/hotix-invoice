# 🔥 HOTIX — Extraction de Factures

> Système local Windows d'extraction automatique des champs de factures scannées (PDF, images) via OCR + IA, avec interface graphique WPF premium.

[![Build](https://github.com/medamineessid/hotix-invoice/actions/workflows/build-check.yml/badge.svg)](https://github.com/medamineessid/hotix-invoice/actions)
[![Sentry](https://img.shields.io/badge/monitoring-Sentry-6B5B95)](https://must-ap.sentry.io)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.txt)

---

## ✨ Fonctionnalités

| Fonctionnalité | Détail |
|---|---|
| **OCR Local** | PaddleOCR (français) — hors ligne, aucune clé API requise |
| **Gemini Vision** | Cloud Google Gemini — **haute précision** (clé API requise) |
| **Grok Vision (xAI)** | Cloud alternative — **précision similaire** (clé API requise) |
| **Sélection de modèle** | Choisissez le modèle AI (gemini-2.5-flash, gemini-1.5-pro, grok-4.3, etc.) |
| **Moteur Automatique** | Gemini → Grok → OCR local (fallback automatique) |
| **Export Excel** | Filtres: Résultats/Missing/Complets + Ajouter au fichier existant |
| **Grille éditable** | Double-clic pour corriger les champs extraits avant export |
| **Multi-langue** | Français / Anglais — bascule en un clic |
| **Glisser-déposer** | Déposez un dossier de factures directement sur la fenêtre |
| **Onboarding** | Visite guidée au premier lancement (spotlight + callout) |
| **Vérification mises à jour** | Notification GitHub Releases (vérification quotidienne) |
| **Thème Premium** | Design system complet — animations, ombres, badges de confiance |

### Engines d'extraction

```
┌─────────────────────────────────────────────────────┐
│   Automatique (recommandé)                          │
│   ├── Gemini Vision  ──┐                            │
│   ├── Grok Vision  ────┤── en cascade               │
│   └── OCR Local  ──────┘ (fallback)                 │
└─────────────────────────────────────────────────────┘
```

---

## 🖼️ Aperçu

| Élément | Description |
|---|---|
| **Barre latérale** | Navigation, version, statut du serveur (point vert) |
| **Panneau de contrôle** | Sélection dossier/fichiers, moteur, bouton d'extraction |
| **Résultats** | DataGrid avec tris, badges de confiance, badge "Local (hors ligne)" |
| **Extractions Incomplètes** | Onglet dédié aux factures avec champs manquants |
| **Aperçu brut** | Texte OCR brut de la ligne sélectionnée |
| **Bannière de synthèse** | Résumé après extraction (succès/échecs) |
| **Barre d'état** | Statut serveur, progression, actions rapides |

---

## 🏗️ Architecture

```
hotix-invoice/
├── server/              ← Python FastAPI — extraction engines
│   ├── main.py          ← Point d'entrée API (FastAPI)
│   ├── models.py        ← Schémas Pydantic
│   ├── ingestion.py     ← Conversion PDF/images → pages
│   ├── ocr_engine.py    ← Wrapper PaddleOCR
│   ├── field_extractor.py ← Extracteur heuristique de champs
│   ├── gemini_extractor.py ← Client Gemini/Grok API
│   ├── utils.py         ← Utilitaires géométrie/texte
│   ├── verify_system.py ← Vérification pré-installation
│   ├── score_accuracy.py ← Évaluation de précision
│   ├── diagnose_invoice.py ← Diagnostic DEBUG
│   ├── generate_test_invoices.py ← Génération factures test
│   ├── appsettings.json ← Configuration (clés API, modèle)
│   └── tests/           ← Tests unitaires pytest
│
├── client/              ← WPF C# — interface utilisateur
│   ├── MainWindow.xaml  ← Fenêtre principale (2 600 lignes)
│   ├── ViewModels/      ← MVVM (MainViewModel, InvoiceRowVM, FileItemVM)
│   ├── Themes/          ← 11 dictionnaires de design system
│   │   ├── Colors.xaml, Brushes.xaml, Spacing.xaml
│   │   ├── Typography.xaml, Animations.xaml
│   │   ├── ButtonStyles.xaml, InputStyles.xaml
│   │   ├── CardStyles.xaml, DataGridStyles.xaml
│   │   ├── DialogStyles.xaml, NavigationStyles.xaml
│   ├── Converters/      ← Value converters (confidence, visibilité)
│   ├── GeminiSetupWindow.xaml ← Configuration API + sélecteur modèle
│   ├── ExportDialog.xaml ← Export Excel avec 3 filtres
│   ├── SplashScreen.xaml ← Écran de démarrage
│   ├── Controls/        ← Contrôles personnalisés (ProgressRing)
│   ├── Resources/       ← Fichiers de traduction (EN/FR)
│   └── HotixDiagnostics/ ← Outil de diagnostic post-installation
│
├── installer/           ← Inno Setup — installateur
│   ├── Hotix.iss        ← Script 600+ lignes Pascal
│   ├── vendor/          ← Python 3.12 + Poppler
│   ├── CRITICAL_ITEMS_ANSWERS.md
│   └── VERIFICATION_REPORT.md
│
├── scripts/             ← Automatisation
│   ├── setup.ps1        ← Configuration machine unique
│   ├── start.ps1        ← Lancement (PowerShell)
│   └── start.bat        ← Lancement (batch)
│
├── requirements.txt     ← Dépendances Python
└── README.md            ← Ce fichier
```

---

## 🚀 Pour les utilisateurs — Guide de démarrage rapide

### Prérequis

| Logiciel | Version | Lien |
|---|---|---|
| Python | 3.8+ (3.12 recommandé) | [python.org](https://www.python.org/downloads/) |
| .NET Desktop Runtime | 8.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| Poppler | Dernière version | [poppler-windows](https://github.com/oschwartz10612/poppler-windows/releases/latest) |

> ⚠️ **Important**: Lors de l'installation de Python, cochez **"Add Python to PATH"**.

### Installation rapide

```powershell
# 1. Cloner le dépôt
git clone https://github.com/medamineessid/hotix-invoice.git
cd hotix-invoice

# 2. Lancer le script de configuration (une seule fois)
.\scripts\setup.ps1

# 3. Publier l'application
cd client
dotnet publish -c Release -o publish
```

### Ou via l'installateur

Téléchargez `HotixSetup_1.0.0.exe` (~50 MB) depuis la page [Releases](https://github.com/medamineessid/hotix-invoice/releases/latest).

L'installateur gère automatiquement :
- ✅ Détection de Python (PATH → registre → installateur intégré)
- ✅ Vérification de la version Python (3.8+)
- ✅ Vérification de la connexion Internet
- ✅ Vérification de l'espace disque (2 200 MB requis)
- ✅ Création de l'environnement virtuel
- ✅ Installation des dépendances Python (3 tentatives avec backoff)
- ✅ Installation de Poppler et configuration PATH
- ✅ Rollback automatique en cas d'échec
- ✅ Journal d'installation détaillé (`{app}\install.log`)
- ✅ Lancement de l'application après installation

### Utilisation quotidienne

1. **Lancement** : Double-cliquez sur le raccourci **HOTIX** (ou `client/publish/Hotix.InvoiceClient.exe`)
2. **Splash** : Écran de démarrage pendant l'initialisation du serveur OCR
3. **Ajout fichiers** : Cliquez **Ajouter >** ou glissez-déposez un dossier
4. **Sélection moteur** : Automatique (recommandé), Gemini, Grok, ou OCR local
5. **Extraction** : Cliquez **Lancer l'extraction** (ou `F5`)
6. **Correction** : Double-cliquez sur les cellules pour éditer
7. **Export** : Cliquez **Exporter en Excel** (ou `Ctrl+E`)

### Raccourcis clavier

| Touche | Action |
|---|---|
| `F5` | Lancer l'extraction |
| `Escape` | Annuler l'extraction |
| `Ctrl+E` | Exporter en Excel |

### Configuration API Gemini / Grok

Pour utiliser les moteurs cloud (précision supérieure) :

1. Cliquez sur l'icône **⚙** à côté du sélecteur de moteur
2. Entrez votre **clé API Gemini** ([Google AI Studio](https://aistudio.google.com/app/apikey))
3. Et/ou votre **clé API Grok** ([xAI](https://x.ai))
4. Sélectionnez le modèle souhaité dans la liste déroulante
5. Cliquez **Enregistrer**

La clé est validée automatiquement avant d'être sauvegardée.

---

## 🔧 Pour les développeurs — Guide technique

### Build du client

```bash
cd client
dotnet restore
dotnet build -c Debug
dotnet publish -c Release -o publish
```

### Lancement du serveur seul

```bash
cd serveur de la racine du projet
python -m uvicorn server.main:app --host 127.0.0.1 --port 8000 --reload
```

### Tests Python

```bash
cd server
pytest tests/ -v
```

### Build de l'installateur

```powershell
# Prérequis : Inno Setup 6.3+
# 1. Publier le client
cd client
dotnet publish -c Release -o publish

# 2. Compiler l'installateur
cd installer
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" Hotix.iss
```

---

## 📊 Tableau de bord technique

### Python Server

| Endpoint | Méthode | Description |
|---|---|---|
| `/health` | GET | Santé du serveur |
| `/engine-status` | GET | Disponibilité Gemini/OCR |
| `/extract` | POST | Extraction d'une facture (multipart) |
| `/validate-gemini-key` | POST | Validation clé API Gemini |
| `/validate-grok-key` | POST | Validation clé API Grok |
| `/admin/recycle-engine` | POST | Recyclage forcé du moteur OCR |

### Modèle de réponse (`InvoiceExtractionResponse`)

```json
{
  "numero_facture": "FAC-2024-001",
  "date": "01/01/2024",
  "fournisseur": "ABC SARL",
  "client": "Client X",
  "montant_ht": "1000.00",
  "tva": "200.00",
  "taxe": "10.00",
  "ttc": "1210.00",
  "confiance": 0.92,
  "texte_brut": "...",
  "engine_used": "gemini"
}
```

### Design System

Le design system HOTIX est composé de **11 dictionnaires de ressources XAML** :

| Fichier | Contenu |
|---|---|
| `Colors.xaml` | Palette — 30 couleurs (fond, texte, accent, fonctionnel, badges) |
| `Brushes.xaml` | Pinceaux + ombres portées Apple-style |
| `Typography.xaml` | Échelle typographique (11px → 32px), 5 graisses |
| `Spacing.xaml` | Grille 8px (4, 8, 12, 16, 24, 32, 40, 48) |
| `Animations.xaml` | 14 storyboards (150-250ms, easing cubique) |
| `ButtonStyles.xaml` | 4 styles + TemplateButtonBase avec hover lift + press scale |
| `InputStyles.xaml` | TextBox, PasswordBox, ComboBox, CheckBox, RadioButton |
| `CardStyles.xaml` | Cartes avec radius 16, hover lift 1px |
| `DataGridStyles.xaml` | Grille premium — en-tête triable, badges pastel, lignes 48px |
| `DialogStyles.xaml` | Modaux avec fade + scale (0.95→1.0, 250ms) |
| `NavigationStyles.xaml` | Barre latérale 240px, nav active soft red |

### Gestion d'erreurs (Sentry)

Le projet utilise **Sentry** pour le monitoring des erreurs :

- **DSN client** : Configuré dans `App.xaml.cs`
- **DSN serveur** : Via `SENTRY_DSN` variable d'environnement
- **Bugs résolus** : DOTNET-A (curseur), DOTNET-B (traduction), DOTNET-C (ToggleButton)

---

## 🧪 Tests

### Tests Python (pytest)

```bash
# Tous les tests
cd server && pytest tests/ -v

# Tests spécifiques
pytest tests/test_field_extractor.py -v
pytest tests/test_ingestion.py -v
pytest tests/test_ocr_engine.py -v
pytest tests/test_utils.py -v
```

### Build client

```bash
cd client
dotnet build -c Debug
# ✅ 0 erreurs, 0 warnings
```

---

## 📋 Scripts utiles

| Script | Usage |
|---|---|
| `scripts/setup.ps1` | Configuration machine unique (venv, dépendances) |
| `scripts/start.ps1` | Lancement serveur + client (PowerShell) |
| `scripts/start.bat` | Lancement rapide (double-clic) |
| `client/rebuild-and-run.bat` | Nettoyage + build + lancement |

---

## 🔒 Sécurité

- Les clés API sont stockées dans `server/appsettings.json` (hors git)
- Le serveur écoute uniquement sur `127.0.0.1:8000` (localhost)
- CORS restreint aux origines localhost, méthodes GET/POST
- Aucune donnée n'est transmise à des serveurs tiers (sauf Gemini/Grok si configuré)
- Journalisation des erreurs via Sentry (DSN configurable)

---

## 📄 License

Ce projet est sous licence MIT. Voir le fichier [LICENSE.txt](LICENSE.txt) pour plus de détails.

---

## 🙏 Remerciements

- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — Moteur OCR local
- [FastAPI](https://fastapi.tiangolo.com/) — Framework API Python
- [ClosedXML](https://closedxml.github.io/ClosedXML/) — Export Excel .NET
- [Inno Setup](https://jrsoftware.org/isinfo.php) — Installateur Windows
- [Sentry](https://sentry.io/) — Monitoring d'erreurs
- [Poppler](https://poppler.freedesktop.org/) — Traitement PDF
