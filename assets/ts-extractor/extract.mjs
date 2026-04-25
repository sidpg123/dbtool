#!/usr/bin/env node
/**
 * Phase 1: TS extractor (JSONL to stdout)
 *
 * Inputs:
 *   --files <path>   newline-delimited list of absolute file paths
 *   --root <path>    repo root (used only for context; C# will relativize)
 *
 * Output: one JSON object per line:
 *   {
 *     sourceKind:
 *       "embedded_raw_sql"
 *       | "typeorm_raw_query"
 *       | "typeorm_query_dynamic"
 *       | "typeorm_query_builder_site",
 *     file, startLine, startCol, endLine, endCol,
 *     sqlText?, completeness?
 *   }
 */

import fs from "node:fs";
import ts from "typescript";

function argValue(flag) {
  const i = process.argv.indexOf(flag);
  if (i === -1) return null;
  return process.argv[i + 1] ?? null;
}

if (process.argv.includes("--help")) {
  process.stdout.write(`
sqlrepoanalyzer ts-extractor

Usage:
  node extract.mjs --files <ts-files.txt> --root <repoRoot>
`);
  process.exit(0);
}

const filesListPath = argValue("--files");
if (!filesListPath) {
  process.stderr.write("Missing --files\\n");
  process.exit(2);
}

const filePaths = fs
  .readFileSync(filesListPath, "utf8")
  .split(/\r?\n/g)
  .map((s) => s.trim())
  .filter(Boolean);

const sqlStart = /^\s*(select|with|insert|update|delete|merge)\b/i;

function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function lineCol(sf, pos) {
  const lc = sf.getLineAndCharacterOfPosition(pos);
  return { line: lc.line + 1, col: lc.character + 1 };
}

function isSqlCandidate(text) {
  if (!text) return false;
  return sqlStart.test(text) || /\bfrom\b/i.test(text);
}

function getTextForLiteral(node, sf) {
  if (ts.isStringLiteralLike(node)) return node.text;
  if (ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
  if (ts.isTemplateLiteral(node)) {
    // Only treat as extractable SQL if there are no substitutions.
    if (node.head && node.templateSpans && node.templateSpans.length === 0) {
      return node.getText(sf).replace(/^`/, "").replace(/`$/, "");
    }
    return null;
  }
  return null;
}

function getCalleeName(expr) {
  if (ts.isPropertyAccessExpression(expr)) return expr.name.text;
  if (ts.isIdentifier(expr)) return expr.text;
  return null;
}

function isPropertyNamedQuery(expr) {
  if (!ts.isPropertyAccessExpression(expr)) return false;
  return expr.name.text === "query";
}

function isLikelyTypeOrmQueryCall(expr) {
  // Most common: dataSource.query / manager.query / queryRunner.query / this.dataSource.query
  // We intentionally avoid matching bare `query(...)` calls.
  if (!ts.isPropertyAccessExpression(expr)) return false;
  if (expr.name.text !== "query") return false;

  const receiver = expr.expression;
  if (ts.isIdentifier(receiver)) {
    const n = receiver.text.toLowerCase();
    return (
      n === "datasource" ||
      n === "manager" ||
      n === "queryrunner" ||
      n === "connection" ||
      n === "ds"
    );
  }

  if (ts.isPropertyAccessExpression(receiver)) {
    const n = receiver.name.text.toLowerCase();
    return n === "datasource" || n === "manager" || n === "queryrunner";
  }

  // Fallback: still allow any *.query if first arg looks like SQL (reduces misses, increases false positives)
  return true;
}

function emitTypeOrmQueryCall(sf, file, node, sqlText, completeness, sourceKind) {
  const start = lineCol(sf, node.getStart(sf));
  const end = lineCol(sf, node.getEnd());
  emit({
    sourceKind,
    file,
    startLine: start.line,
    startCol: start.col,
    endLine: end.line,
    endCol: end.col,
    sqlText,
    completeness,
  });
}

for (const file of filePaths) {
  let content;
  try {
    content = fs.readFileSync(file, "utf8");
  } catch {
    continue;
  }

  const sf = ts.createSourceFile(file, content, ts.ScriptTarget.Latest, true);

  function visit(node) {
    // Embedded raw SQL in string/template literals
    if (ts.isStringLiteralLike(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      const text = node.text;
      if (isSqlCandidate(text)) {
        const start = lineCol(sf, node.getStart(sf));
        const end = lineCol(sf, node.getEnd());
        emit({
          sourceKind: "embedded_raw_sql",
          file,
          startLine: start.line,
          startCol: start.col,
          endLine: end.line,
          endCol: end.col,
          sqlText: text,
          completeness: "full",
        });
      }
    }

    // TypeORM: *.query("SQL ...")
    if (ts.isCallExpression(node)) {
      const calleeExpr = node.expression;
      const calleeName = getCalleeName(calleeExpr);

      if (isPropertyNamedQuery(calleeExpr) && node.arguments.length >= 1) {
        const sqlArg = node.arguments[0];

        // Dynamic SQL: template with substitutions, concatenation, non-literal first arg, etc.
        if (ts.isTemplateLiteral(sqlArg) && sqlArg.templateSpans && sqlArg.templateSpans.length > 0) {
          if (isLikelyTypeOrmQueryCall(calleeExpr)) {
            emitTypeOrmQueryCall(sf, file, node, null, "partial", "typeorm_query_dynamic");
          }
        } else {
          const text = getTextForLiteral(sqlArg, sf);
          if (text && isSqlCandidate(text) && isLikelyTypeOrmQueryCall(calleeExpr)) {
            const start = lineCol(sf, sqlArg.getStart(sf));
            const end = lineCol(sf, sqlArg.getEnd());
            emit({
              sourceKind: "typeorm_raw_query",
              file,
              startLine: start.line,
              startCol: start.col,
              endLine: end.line,
              endCol: end.col,
              sqlText: text,
              completeness: "full",
            });
          } else if (!text && isLikelyTypeOrmQueryCall(calleeExpr)) {
            emitTypeOrmQueryCall(sf, file, node, null, "partial", "typeorm_query_dynamic");
          }
        }
      }

      // QueryBuilder site marker
      if (calleeName === "createQueryBuilder") {
        const start = lineCol(sf, node.getStart(sf));
        const end = lineCol(sf, node.getEnd());
        emit({
          sourceKind: "typeorm_query_builder_site",
          file,
          startLine: start.line,
          startCol: start.col,
          endLine: end.line,
          endCol: end.col,
          sqlText: null,
          completeness: "partial",
        });
      }
    }

    ts.forEachChild(node, visit);
  }

  visit(sf);
}

