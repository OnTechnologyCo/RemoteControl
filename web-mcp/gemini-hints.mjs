export const hintsMetadataKey = 'remotecontrol/internalGeminiReadOnlyHints';

export function applyGeminiHints(tools, overrides) {
  // Never modify even nested annotations/_meta on the shared catalog or file tool.
  const result = structuredClone(tools);
  if (!overrides || typeof overrides !== 'object' || Array.isArray(overrides) || Buffer.byteLength(JSON.stringify(overrides)) > 16384)
    throw Error('Invalid Gemini readOnlyHint snapshot');
  for (const [name, value] of Object.entries(overrides)) {
    if (name === 'pc_set_gemini_settings') continue; // Migrate legacy overrides; this mutation is always readOnlyHint=false.
    if (typeof value !== 'boolean') throw Error('Invalid Gemini readOnlyHint value');
    const tool = result.find(tool => tool.name === name);
    if (!tool) continue; // A retired tool must not prevent schema refresh after upgrade.
    const original = tool.annotations?.readOnlyHint;
    tool.annotations = {...tool.annotations, readOnlyHint:value};
    tool._meta = {...tool._meta, 'remotecontrol/defaultReadOnlyHint':original,
      'remotecontrol/readOnlyHintOverride':true};
    if (original !== value) tool.description += `\nAdministrator override: readOnlyHint=${value} (server default: ${original}). Actual behavior, side effects and execution permissions are unchanged. This metadata does not grant Gemini App actions permission.`;
  }
  return result;
}
