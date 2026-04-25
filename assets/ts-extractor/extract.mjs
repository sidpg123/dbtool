#!/usr/bin/env node
/**
 * Phase 0 placeholder.
 *
 * Phase 1 will use the TypeScript compiler API to emit JSONL records:
 * - embedded SQL string/template literals
 * - TypeORM .query() SQL
 * - createQueryBuilder clause list (partial LQR) + completeness flags
 *
 * For now, this script only implements `--help` and exits.
 */

if (process.argv.includes("--help") || process.argv.length <= 2) {
  process.stdout.write(`
sqlrepoanalyzer ts-extractor (Phase 0)

This is a placeholder. Phase 1 will emit JSONL to stdout.
`);
  process.exit(0);
}

process.stderr.write("Phase 0: extractor not implemented yet. Use --help.\\n");
process.exit(2);

