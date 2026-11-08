# AudioTranscript

Windows desktop application: C# / .NET Framework WinForms frontend and a frozen
Python 3.12 / faster-whisper backend. This is a local application with no API calls.

`AudioTranscript.cs` implements the desktop window, multi-file queue, drag and drop,
progress, cancellation, UTF-8 JSON-lines process communication, and results access.
`engine.py` loads `../model` supplied by the frontend as an absolute local path.
The model is not embedded in the EXE. Pass `--request <JSON file>` to the engine.
Required JSON fields: `model`, `files`; optional: `output`, `language`, `prompt`.
Optional `clip` is for validation only. The desktop frontend transcribes entire files.
Results are reserved using exclusive creation and are only renamed from `.partial`
after recognition completes. Duplicate source filenames receive numbered results.

## Build

Use a normal Windows Python 3.12 x64 environment. Install requirements-build.txt.
In faster_whisper/audio.py remove the unsupported argument
`, metadata_errors="ignore"` from the `av.open(...)` call when using PyAV 19.0.2.
The delivered binary contains this compatibility adjustment and has been tested
with actual audio through the frozen engine and desktop frontend.

Build the engine:

```powershell
python -m PyInstaller --noconfirm --onedir --console --name engine --collect-all faster_whisper --collect-all ctranslate2 --collect-all av --collect-all tokenizers --collect-all onnxruntime --copy-metadata huggingface-hub --copy-metadata tqdm --exclude-module tkinter --exclude-module torch --exclude-module tensorflow engine.py
```

Build the frontend with the Windows .NET Framework compiler:

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /out:АудиоТранскрипт.exe AudioTranscript.cs
```

Place the generated `engine` directory beside the frontend. In `model`, place
`model.bin`, `config.json`, `tokenizer.json`, `vocabulary.txt` from
`Systran/faster-whisper-medium`. Keep its weights compatible with CTranslate2.
Dependencies and the model retain their respective upstream licenses.

The app uses deterministic decoding, beam size 3, int8 CPU inference, no temperature
fallback, and a no-repeat-ngram limit of 3. There is no semantic post-editing or
speaker diarization. Transcripts are approximate, including their timestamps.
