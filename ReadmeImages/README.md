# Captures d'écran du README

Captures de la démo d'AlpenMux, au format WebP. Elles sont utilisées par le [README principal](../README.md).

| Fichier | Page | Contenu |
|---|---|---|
| `install.webp` | Installation | Pilote NVIDIA, environnement Python, vLLM, vLLM-Omni, outils optionnels, llama.cpp, stable-diffusion.cpp |
| `models_local.webp` | Modèles → Mes modèles | Modèles présents sur le disque, avec architecture, quantification, VRAM estimée et boutons Lancer / Ouvrir / Supprimer |
| `models_HF.webp` | Modèles → Télécharger | Recherche Hugging Face, choix des variantes GGUF, taille à télécharger |
| `Server.webp` | Serveurs | Liste des serveurs (prêts, arrêtés, en démarrage), console vLLM du serveur sélectionné |
| `ServerEdit.webp` | Serveurs → Nouveau serveur | Formulaire de lancement : VRAM et RAM prévues, part du modèle sur le GPU, réglages réseau, fonctions du modèle |
| `chat.webp` | Chat | Discussion avec une base de connaissances (RAG) : réponses citant leurs sources |
| `RAG.webp` | Connaissances | Bases de connaissances, serveur d'embedding, indexation, zone de test de recherche |
| `Image.webp` | Images | Galerie de générations et réglages (taille, étapes, guidance, prompt négatif) |
| `Image_detail.webp` | Images → carrousel | Détail d'une image : prompt, réglages, modèle, serveur, durée |
| `audio.webp` | Audio | Synthèse vocale : texte, voix, langue, audios générés |
| `moniteur.webp` | Moniteur | GPU, VRAM, CPU, RAM, disque des modèles, processus GPU |
| `Settings.webp` | Paramètres | Apparence, emplacements, serveur, adresse API unique, moniteur système |

## Modèles d'IA utilisés pour la démo

Les modèles ci-dessous sont ceux qui apparaissent dans les captures (lancés ou visibles dans les listes).
Les noms sont ceux affichés par l'application.

### Modèles lancés pendant la démo

| Modèle | Type | Moteur | Où on le voit | Détail |
|---|---|---|---|---|
| **gemma-4-12B-it-qat-q4_0** (GGUF) | Texte (chat) | llama.cpp, port 8001 | `chat.webp`, `Server.webp`, `models_local.webp` | Quantification GGUF Q4_0, 6,5 Gio. Dépôt : `google/gemma-4-12B-it-qat-q4_0-gguf` |
| **Qwen3-Embedding-4B-GGUF** | Embeddings (RAG) | llama.cpp en mode `--embedding`, port 8011 | `RAG.webp`, `Server.webp`, `chat.webp` | Quantification Q5_K_M, 2,7 Gio. Dépôt : `Qwen/Qwen3-Embedding-4B-GGUF` |
| **FLUX.2-klein-4b-fp8** | Génération d'images | vLLM-Omni, port 8000 | `Image.webp`, `Image_detail.webp` | Taille 1024×1024, guidance 1, étapes automatiques. Images générées : 44,5 s pour la dernière |
| **Qwen3-TTS-12Hz-1.7B-CustomVoice** | Synthèse vocale | vLLM-Omni | `audio.webp`, `moniteur.webp`, `Server.webp` (favori « QwenTTS-1.7B ») | Voix `ryan` et `aiden`, langue française, 9 voix proposées par le serveur |
| **LFM2.5-1.2B-Instruct** | Texte | vLLM, port 8000 | `Server.webp` (console), `models_local.webp` | Dépôt : `LiquidAI/LFM2.5-1.2B-Instruct`. Arrêté au moment de la capture |
| **LFM2.5-350M** | Texte | vLLM | `ServerEdit.webp` | Formulaire de lancement : contexte limité à 32 768 tokens, `--tool-call-parser lfm2` |

### Autres modèles visibles dans les captures (non lancés pour la démo)

| Modèle | Où on le voit | Remarque |
|---|---|---|
| Qwen3-Embedding-0.6B-GGUF | `Server.webp`, `models_local.webp` | Serveur arrêté |
| Qwen3-Embedding-4B (format vLLM, dossier) | `models_local.webp` | Présent sur le disque |
| Qwen3-4B | `models_local.webp` | Présent sur le disque |
| Qwen3-8B-GGUF | `models_local.webp` | Présent sur le disque |
| waillIllustriousSDXL_v170 (SDXL) | `models_local.webp` | Fichier unique, lançable avec vLLM-Omni ou stable-diffusion.cpp |
| Apertus-8B-Instruct-2509-GGUF | `models_HF.webp` | Résultat de recherche Hugging Face (quantification Q4_K_M cochée, aucun téléchargement lancé) |

### Base de connaissances de la démo

Le RAG de `chat.webp` et `RAG.webp` s'appuie sur un seul document, **Journal officiel n° 35** (PDF, 5 octobre 2023),
indexé dans la base « JURA » (71 passages). Le modèle d'embedding est Qwen3-Embedding-4B-GGUF. La base « PIC »
(818 passages) apparaît aussi dans la liste.
