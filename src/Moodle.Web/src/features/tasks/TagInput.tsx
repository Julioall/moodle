import { useMemo, useState } from "react";
import { X } from "lucide-react";

export type TagSuggestion = {
  value: string;
  label: string;
  category?: "school" | "course" | "student" | "generic";
  hint?: string;
};

type TagInputProps = {
  values: string[];
  onChange: (values: string[]) => void;
  suggestions?: TagSuggestion[];
  placeholder?: string;
  helperText?: string;
  ariaLabel?: string;
};

const colorClasses: Record<NonNullable<TagSuggestion["category"]>, string> = {
  school: "border-violet-200 bg-violet-50 text-violet-700",
  course: "border-sky-200 bg-sky-50 text-sky-700",
  student: "border-emerald-200 bg-emerald-50 text-emerald-700",
  generic: "border-primary/20 bg-primary/10 text-primary",
};

function colorForTag(tag: string, category?: TagSuggestion["category"]) {
  if (category) return colorClasses[category];
  const colors = Object.values(colorClasses);
  let hash = 0;
  for (const character of tag) hash = (hash * 31 + character.charCodeAt(0)) | 0;
  return colors[Math.abs(hash) % colors.length];
}

function normalizeValue(value: string, suggestions: TagSuggestion[]) {
  const trimmed = value.trim().replace(/\s+/g, " ");
  if (!trimmed) return { value: "", category: undefined };
  const separator = trimmed.indexOf(":");
  const prefix = separator >= 0 ? trimmed.slice(0, separator).toLowerCase() : "";
  const query = separator >= 0 ? trimmed.slice(separator + 1).trim() : trimmed;
  const selected = suggestions.find(
    (suggestion) =>
      suggestion.value.toLowerCase() === trimmed.toLowerCase() ||
      suggestion.label.toLowerCase() === query.toLowerCase(),
  );
  if (selected) return { value: selected.label.trim(), category: selected.category };
  if (["escola", "aluno", "curso"].includes(prefix) && query) {
    return { value: query, category: prefix === "escola" ? "school" : prefix === "curso" ? "course" : "student" } as const;
  }
  return { value: trimmed, category: "generic" as const };
}

export function TagInput({
  values,
  onChange,
  suggestions = [],
  placeholder = "Adicionar tag…",
  helperText = "Use espaço, Enter ou vírgula para adicionar.",
  ariaLabel = "Tags",
}: TagInputProps) {
  const [draft, setDraft] = useState("");
  const [categories, setCategories] = useState<Record<string, TagSuggestion["category"]>>({});
  const prefix = draft.includes(":") ? draft.slice(0, draft.indexOf(":")).trim().toLowerCase() : "";
  const query = draft.includes(":") ? draft.slice(draft.indexOf(":") + 1).trim().toLowerCase() : draft.trim().toLowerCase();
  const visibleSuggestions = useMemo(
    () =>
      suggestions
        .filter((suggestion) => !prefix || !["escola", "aluno", "curso"].includes(prefix) || (prefix === "escola" && suggestion.category === "school") || (prefix === "curso" && suggestion.category === "course") || (prefix === "aluno" && suggestion.category === "student"))
        .filter((suggestion) => !query || `${suggestion.label} ${suggestion.value}`.toLowerCase().includes(query))
        .slice(0, 7),
    [prefix, query, suggestions],
  );
  const add = (rawValue: string, category?: TagSuggestion["category"]) => {
    const normalized = normalizeValue(rawValue, suggestions);
    const value = normalized.value;
    if (!value || values.some((item) => item.toLowerCase() === value.toLowerCase())) return;
    onChange([...values, value]);
    setCategories((current) => ({ ...current, [value]: category ?? normalized.category ?? "generic" }));
    setDraft("");
  };

  return (
    <div className="relative grid gap-1.5">
      <div className="flex min-h-10 flex-wrap items-center gap-1.5 rounded-md border border-input bg-background px-2 py-1.5 focus-within:ring-2 focus-within:ring-ring">
        {values.map((tag) => (
          <span key={tag} className={`inline-flex items-center gap-1 rounded-full border px-2 py-1 text-xs font-medium ${colorForTag(tag, categories[tag])}`}>
            {tag}
            <button type="button" className="rounded-full p-0.5 hover:bg-black/10" onClick={() => onChange(values.filter((item) => item !== tag))} aria-label={`Remover ${tag}`}>
              <X className="h-3 w-3" />
            </button>
          </span>
        ))}
        <input
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            const prefixOnly = /^(escola|aluno|curso):\s*$/i.test(draft);
            if (event.key === "Enter" || event.key === "," || (event.key === " " && draft.trim() && !prefixOnly)) {
              event.preventDefault();
              add(draft);
            }
            if (event.key === "Backspace" && !draft && values.length) onChange(values.slice(0, -1));
          }}
          className="min-w-[12rem] flex-1 bg-transparent px-1 py-1 text-sm outline-none placeholder:text-muted-foreground"
          placeholder={placeholder}
          aria-label={ariaLabel}
        />
      </div>
      {prefix && ["escola", "aluno", "curso"].includes(prefix) && visibleSuggestions.length > 0 && (
        <div className="absolute left-0 right-0 top-full z-20 mt-1 max-h-56 overflow-auto rounded-md border bg-popover p-1 shadow-lg">
          {visibleSuggestions.map((suggestion) => (
            <button key={`${suggestion.category ?? "generic"}:${suggestion.value}`} type="button" className="flex w-full items-center justify-between rounded px-2 py-2 text-left text-sm hover:bg-accent" onMouseDown={(event) => event.preventDefault()} onClick={() => add(suggestion.value, suggestion.category)}>
              <span>{suggestion.label}</span>
              {suggestion.hint && <span className="ml-3 text-xs text-muted-foreground">{suggestion.hint}</span>}
            </button>
          ))}
        </div>
      )}
      <span className="text-[11px] font-normal text-muted-foreground">{helperText}</span>
    </div>
  );
}
