/*
 * Everything of Monaco this app uses, and nothing else.
 *
 * <b>Cherry-picked, not `import 'monaco-editor'`.</b> The package's main entry registers every
 * feature and eighty languages, which is several times what a schema editor needs and is loaded
 * by the first visitor to this screen. What follows is `editor.main`'s own list with the parts a
 * JSON/Protobuf editor has no use for removed: no code actions, no inlay hints, no rename, no
 * peek views, no suggestions, no comment toggling — a comment is a syntax error in the registry's
 * reader, and a keystroke that inserts one is a way to make a document the registry refuses.
 * What stays is what editing text needs: find, folding, multi-cursor, bracket matching,
 * hover (which is how a marker's message is read), go-to-next-problem, and the Unicode
 * highlighter — which marks the look-alike characters that make `ordеr` with a Cyrillic "е" a
 * different subject from the one it appears to be.
 *
 * Each feature is imported through the package's own `features/<name>/register` entry rather
 * than from deep inside `editor/contrib`: those are the entries the package itself builds
 * `editor.main` from, they carry the stylesheet a feature needs, and `find`'s applies a
 * correction that stops its hidden widget being reachable by Tab.
 *
 * Imported only through `monaco-loader.ts`, which is itself only ever imported dynamically: this
 * module is the lazy chunk, and nothing eager may import it.
 */
import * as monaco from 'monaco-editor/editor/editor.api';

import 'monaco-editor/editor/browser/coreCommands';
import 'monaco-editor/editor/common/standaloneStrings';
import 'monaco-editor/features/bracketMatching/register';
import 'monaco-editor/features/clipboard/register';
import 'monaco-editor/features/codeEditor/register';
import 'monaco-editor/features/codicon/register';
import 'monaco-editor/features/contextmenu/register';
import 'monaco-editor/features/cursorUndo/register';
import 'monaco-editor/features/find/register';
import 'monaco-editor/features/folding/register';
import 'monaco-editor/features/gotoError/register';
import 'monaco-editor/features/hover/register';
import 'monaco-editor/features/indentation/register';
import 'monaco-editor/features/linesOperations/register';
import 'monaco-editor/features/multicursor/register';
import 'monaco-editor/features/toggleTabFocusMode/register';
import 'monaco-editor/features/unicodeHighlighter/register';
import 'monaco-editor/features/wordHighlighter/register';
import 'monaco-editor/features/wordOperations/register';

// Protobuf is the one language Monaco ships a tokenizer for that a registry subject can be in.
import 'monaco-editor/languages/definitions/protobuf/register';

export { monaco };
