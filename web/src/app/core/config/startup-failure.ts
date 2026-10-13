/**
 * Says, in the page, that the app could not start and why.
 *
 * Drawn by hand into the root element because it is for the case where Angular never started:
 * the configuration the deployment supplied was present and wrong, so there is no application
 * to render a component in. `textContent` throughout — the message names a value that came out
 * of a file, and a value that came out of a file is not markup.
 */
export function showStartupFailure(root: Element, message: string): void {
  const container = root.ownerDocument.createElement('main');
  container.setAttribute('role', 'alert');
  container.style.cssText =
    'max-width: 40rem; margin: 4rem auto; padding: 0 1.5rem; font-family: system-ui, sans-serif; line-height: 1.5;';

  const heading = root.ownerDocument.createElement('h1');
  heading.textContent = 'Concordat could not start';
  heading.style.cssText = 'font-size: 1.5rem; margin: 0 0 0.75rem;';

  const detail = root.ownerDocument.createElement('p');
  detail.textContent = message;

  const hint = root.ownerDocument.createElement('p');
  hint.textContent =
    'This is the deployment’s configuration (config.json), not your browser. Whoever runs this ' +
    'registry can correct it; nothing has been sent anywhere.';
  hint.style.cssText = 'opacity: 0.7;';

  container.append(heading, detail, hint);
  root.replaceChildren(container);
}
