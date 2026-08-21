#!/bin/sh
# Regenerate the docs sitemap.
#
#   sh scripts/make-sitemap.sh [DOCSDIR] [BASEURL]
#
# Every *.html under DOCSDIR except 404.html gets a <url> entry; index.html
# becomes the directory URL. <lastmod> is the file's last git commit date, so
# it tracks real changes - run with fetch-depth: 0 in CI. A file that is
# dirty or untracked falls back to its mtime, so a local run before a commit
# still stamps today.
#
# THE BASE URL IS DEFINED IN ONE PLACE, here and in the .ps1 twin, and the
# Pages workflow passes no override. When the site's address is settled,
# this is the line to change.
set -eu
DOCS="${1:-docs}"
BASE="${2:-https://shoddymills.github.io/shoddy-fettler/}"
OUT="$DOCS/sitemap.xml"

{
    printf '<?xml version="1.0" encoding="UTF-8"?>\n'
    printf '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
    find "$DOCS" -name '*.html' ! -name '404.html' | LC_ALL=C sort |
    while read -r f; do
        rel="${f#"$DOCS"/}"
        case "$rel" in
            index.html)   loc="$BASE" ;;
            */index.html) loc="$BASE${rel%index.html}" ;;
            *)            loc="$BASE$rel" ;;
        esac
        if git ls-files --error-unmatch "$f" >/dev/null 2>&1 \
           && [ -z "$(git status --porcelain -- "$f")" ]; then
            mod=$(git log -1 --format=%cs -- "$f")
        else
            mod=$(date -r "$f" +%F 2>/dev/null || date +%F)
        fi
        printf '  <url><loc>%s</loc><lastmod>%s</lastmod></url>\n' "$loc" "$mod"
    done
    printf '</urlset>\n'
} > "$OUT.tmp"
mv "$OUT.tmp" "$OUT"
echo "wrote $OUT"
