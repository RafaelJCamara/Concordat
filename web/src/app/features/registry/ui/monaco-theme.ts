import type { MonacoApi } from './monaco-loader';

/**
 * Turns one of the app's colour tokens into something Monaco can parse.
 *
 * <b>Through a canvas, because Monaco does not read CSS.</b> The tokens are written as
 * `hsl(222 47% 6%)` — the space-separated form — and Monaco's colour parser takes `#rrggbb`,
 * `rgb()` and the comma form of `hsl()`, so handing it the token verbatim silently produces no
 * colour and leaves the editor in Monaco's own grey. A canvas will take any CSS colour and hand
 * back `#rrggbb`, which is the one conversion that does not depend on re-implementing a colour
 * parser here.
 *
 * Null when the token is absent or the environment has no canvas, in which case the theme's own
 * default stands: an editor in the wrong grey is a cosmetic fault, and not worth a failure.
 */
export function resolveColour(token: string, alpha = ''): string | null {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(token).trim();

  if (raw === '') {
    return null;
  }

  const context = document.createElement('canvas').getContext('2d');

  if (context === null) {
    return null;
  }

  // A sentinel first, so a value the canvas rejects is not mistaken for the colour it kept.
  context.fillStyle = '#010203';
  context.fillStyle = raw;

  const resolved = context.fillStyle;

  return typeof resolved === 'string' && resolved.startsWith('#') && resolved !== '#010203'
    ? `${resolved}${alpha}`
    : null;
}

/**
 * Defines and applies the editor's theme from the page's own tokens, so the editor sits in the
 * interface rather than on it as a black rectangle in the light theme.
 */
export function applyEditorTheme(monaco: MonacoApi, dark: boolean): void {
  const colours: Record<string, string> = {};

  const set = (key: string, token: string, alpha = ''): void => {
    const value = resolveColour(token, alpha);

    if (value !== null) {
      colours[key] = value;
    }
  };

  set('editor.background', '--background');
  set('editor.foreground', '--foreground');
  set('editorLineNumber.foreground', '--muted-foreground');
  set('editorLineNumber.activeForeground', '--foreground');
  set('editor.lineHighlightBackground', '--muted', '80');
  set('editor.lineHighlightBorder', '--background');
  set('editorCursor.foreground', '--primary');
  set('editor.selectionBackground', '--primary', '40');
  set('editor.inactiveSelectionBackground', '--primary', '22');
  set('editorWidget.background', '--card');
  set('editorWidget.border', '--border');
  set('editorHoverWidget.background', '--card');
  set('editorHoverWidget.border', '--border');
  set('editorSuggestWidget.background', '--card');
  set('input.background', '--background');
  set('input.border', '--border');
  set('focusBorder', '--ring');
  set('scrollbarSlider.background', '--muted-foreground', '33');
  set('scrollbarSlider.hoverBackground', '--muted-foreground', '55');
  set('editorIndentGuide.background1', '--border');
  set('editorBracketMatch.border', '--primary');

  const name = dark ? 'concordat-dark' : 'concordat-light';

  monaco.editor.defineTheme(name, {
    base: dark ? 'vs-dark' : 'vs',
    inherit: true,
    rules: [],
    colors: colours,
  });
  monaco.editor.setTheme(name);
}
