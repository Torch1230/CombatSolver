"""Delete published/duplicate community reports and their server archives.

Run inside the report-service container. Read a publication receipt from stdin.
"""
import argparse
import json
from pathlib import Path
import re
import sqlite3


def validate_receipt(receipt):
    repository = receipt['repository']
    if repository != 'Torch1230/CombatSolver':
        raise ValueError('Unexpected repository')
    prefix = 'https://github.com/' + repository + '/'
    entries = receipt['entries']
    if not entries:
        raise ValueError('Empty publication receipt')
    ids = set()
    for entry in entries:
        report_id = entry['reportId']
        if not re.fullmatch(r'[0-9a-f]{30,32}', report_id) or report_id in ids:
            raise ValueError('Invalid or duplicate report ID: ' + report_id)
        ids.add(report_id)
        if not re.fullmatch(r'[BQ]\d{3}', entry['batchId']):
            raise ValueError('Invalid batch ID')
        if not entry['issueUrl'].startswith(prefix + 'issues/'):
            raise ValueError('Missing issue receipt')
        if not entry['assetUrl'].startswith(prefix + 'releases/download/'):
            raise ValueError('Missing asset receipt')
        if not entry['assetUrl'].endswith('/' + entry['batchId'] + '.zip'):
            raise ValueError('Asset does not belong to batch')
        if entry['assetId'] <= 0 or entry['assetSizeBytes'] <= 0:
            raise ValueError('Incomplete asset receipt')
        reason = entry.get('reason', 'published')
        if reason not in {'published', 'duplicate_theme'}:
            raise ValueError('Invalid retirement reason')
        if reason == 'duplicate_theme' and not re.fullmatch(r'[TO]\d{3}', entry['themeId']):
            raise ValueError('Duplicate retirement requires a published theme ID')
    return entries


def retire(conn, storage_root, receipt, delete_remote, dry_run=False):
    entries = validate_receipt(receipt)
    root = Path(storage_root).resolve(strict=True)
    selected = []
    # Validate every selected path before removing any archive.
    for entry in entries:
        row = conn.execute('SELECT file_path,cos_key,resolved,mod_version FROM reports WHERE id=?',
                           (entry['reportId'],)).fetchone()
        if row is None:
            selected.append((entry, None))
            continue
        if entry.get('reason') == 'duplicate_theme' and not (row[3] or '').startswith('0.47.'):
            raise ValueError('Duplicate archive outside current 0.47.x window: ' + entry['reportId'])
        file_path = Path(row[0]).resolve()
        if not file_path.is_relative_to(root) or file_path == root or file_path.suffix != '.zip':
            raise ValueError('Archive path outside storage or not a ZIP: ' + entry['reportId'])
        if file_path.exists() and not file_path.is_file():
            raise ValueError('Archive is not a regular file: ' + entry['reportId'])
        selected.append((entry, file_path))
    results = []
    for entry, file_path in selected:
        report_id = entry['reportId']
        if file_path is None:
            results.append({'reportId': report_id, 'action': 'already_absent',
                            'reportRetained': False, 'reportDeleted': False})
            continue
        if dry_run:
            results.append({'reportId': report_id, 'action': 'would_retire',
                            'localExists': file_path.exists()})
            continue
        with conn:
            conn.execute('BEGIN IMMEDIATE')
            current = conn.execute('SELECT file_path,cos_key,resolved FROM reports WHERE id=?',
                                   (report_id,)).fetchone()
            if current is None or Path(current[0]).resolve() != file_path:
                raise ValueError('Report archive changed: ' + report_id)
            remote_key, resolved = current[1:3]
            if remote_key:
                delete_remote(remote_key)
            existed = file_path.exists()
            if existed:
                file_path.unlink()
            reason = entry.get('reason', 'published')
            conn.execute('DELETE FROM reports WHERE id=?', (report_id,))
            conn.execute("INSERT INTO meta (key,value) VALUES ('deleted_count',1) "
                         "ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1")
            results.append({'reportId': report_id, 'batchId': entry['batchId'],
                            'localDeleted': existed, 'cosDeleted': bool(remote_key),
                            'reportRetained': False, 'reportDeleted': True,
                            'resolvedBeforeDeletion': bool(resolved), 'reason': reason,
                            'themeId': entry.get('themeId')})
    return results


def main():
    import sys
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    import config
    import cos_storage
    receipt = json.load(sys.stdin)
    conn = sqlite3.connect(config.DB_PATH)
    conn.execute('PRAGMA foreign_keys=ON')

    def delete_remote(key):
        if not cos_storage.enabled:
            raise RuntimeError('COS key present but COS configuration unavailable')
        # Use the SDK directly: the service helper logs and suppresses deletion errors.
        cos_storage._client_instance().delete_object(Bucket=cos_storage.COS_BUCKET, Key=key)

    result = retire(conn, config.STORAGE_ROOT, receipt, delete_remote, args.dry_run)
    print(json.dumps({'dryRun': args.dry_run, 'results': result}, ensure_ascii=False))


if __name__ == '__main__':
    main()
