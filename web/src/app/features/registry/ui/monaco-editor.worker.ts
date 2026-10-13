/// <reference lib="webworker" />

// Monaco's own worker, unchanged. It exists as a file of its own because the build only
// recognises `new Worker(new URL('./file', import.meta.url))` for a path inside the project,
// and Monaco's worker lives inside a package.
import 'monaco-editor/editor/editor.worker';
