"""Offline transcription engine. UTF-8 JSON lines on stdout; no network calls."""
import argparse
import json
import os
from pathlib import Path
import sys
import traceback
from datetime import datetime

os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['HF_HUB_DISABLE_TELEMETRY'] = '1'
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace', line_buffering=True)
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')


def emit(kind, **data):
    print(json.dumps(dict(type=kind, **data), ensure_ascii=False), flush=True)


def timestamp(value, srt=False):
    ms = max(0, round(value * 1000))
    whole, milli = divmod(ms, 1000)
    hours, rest = divmod(whole, 3600)
    minutes, seconds = divmod(rest, 60)
    text = f'{hours:02d}:{minutes:02d}:{seconds:02d}'
    return text + f',{milli:03d}' if srt else text


def reserve_outputs(folder, stem):
    folder.mkdir(parents=True, exist_ok=True)
    for index in range(10000):
        name = stem + '_транскрипт' + (f'_{index + 1}' if index else '')
        txt = folder / (name + '.txt')
        srt = folder / (name + '.srt')
        partial = folder / (name + '.partial.txt')
        partial_srt = folder / (name + '.partial.srt')
        if any(p.exists() for p in (txt, srt, partial, partial_srt)):
            continue
        try:
            partial.open('x', encoding='utf-8-sig').close()
        except FileExistsError:
            continue
        return txt, srt, partial, partial_srt
    raise RuntimeError('Не удалось подобрать свободное имя для результата.')


def run(request):
    from faster_whisper import WhisperModel

    model_dir = Path(request['model']).resolve()
    for name in ('model.bin', 'config.json', 'tokenizer.json', 'vocabulary.txt'):
        if not (model_dir / name).is_file():
            raise RuntimeError('Отсутствует файл модели: ' + str(model_dir / name))
    inputs = request.get('files', [])
    if not inputs:
        raise RuntimeError('Не выбрана ни одна запись.')
    emit('loading', message='Загрузка модели. Первый запуск может занять некоторое время.')
    model = WhisperModel(str(model_dir), device='cpu', compute_type='int8',
                         cpu_threads=min(8, max(1, os.cpu_count() or 4)),
                         local_files_only=True)
    failures = 0
    completed = 0
    for index, source_text in enumerate(inputs):
        source = Path(source_text).resolve()
        partial = None
        try:
            if not source.is_file():
                raise RuntimeError('Файл не найден: ' + str(source))
            folder = Path(request['output']) if request.get('output') else source.parent / 'Транскрипты'
            options = dict(language=request.get('language', 'ru'), beam_size=3,
                           temperature=0.0, no_repeat_ngram_size=3, vad_filter=False,
                           initial_prompt=request.get('prompt', '').strip() or None)
            # Only used by command-line validation; the desktop app always uses the whole file.
            if request.get('clip'):
                options['clip_timestamps'] = request['clip']
            segments, info = model.transcribe(str(source), **options)
            txt, srt, partial, partial_srt = reserve_outputs(folder, source.stem)
            emit('started', index=index, total=len(inputs), duration=info.duration,
                 file=str(source), partial=str(partial))
            count = 0
            with partial.open('w', encoding='utf-8-sig') as text_out, partial_srt.open('x', encoding='utf-8-sig') as sub_out:
                text_out.write('ТРАНСКРИПТ АУДИОЗАПИСИ\n' + source.name + '\n')
                text_out.write('Создан: ' + datetime.now().strftime('%d.%m.%Y %H:%M') + '\n')
                text_out.write('Длительность записи: ' + timestamp(info.duration) + '\n\n')
                text_out.write('Автоматическое распознавание. Имена, сокращения и отдельные слова могут содержать ошибки. Таймкоды указывают начало фрагментов.\n\n')
                text_out.flush()
                for segment in segments:
                    content = segment.text.strip()
                    if not content:
                        continue
                    count += 1
                    text_out.write(f'[{timestamp(segment.start)}] {content}\n')
                    sub_out.write(f'{count}\n{timestamp(segment.start, True)} --> {timestamp(segment.end, True)}\n{content}\n\n')
                    text_out.flush()
                    sub_out.flush()
                    emit('progress', index=index, total=len(inputs),
                         seconds=segment.end, duration=info.duration)
                if count == 0:
                    text_out.write('[Речь не распознана.]\n')
                text_out.write('\n[Конец транскрипта]\n')
            # Reservations prevent overwriting results from earlier runs or concurrent instances.
            partial.rename(txt)
            partial_srt.rename(srt)
            completed += 1
            emit('result', file=str(source), txt=str(txt), srt=str(srt), segments=count)
        except Exception as exc:
            failures += 1
            emit('file_error', file=str(source), message=str(exc),
                 partial=str(partial) if partial and partial.exists() else '')
            traceback.print_exc(file=sys.stderr)
    emit('done', completed=completed, failed=failures)
    return 1 if failures else 0


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    args = parser.parse_args()
    try:
        with open(args.request, encoding='utf-8-sig') as config:
            code = run(json.load(config))
    except Exception as exc:
        emit('fatal', message=str(exc))
        traceback.print_exc(file=sys.stderr)
        code = 2
    sys.exit(code)
