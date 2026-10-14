# Changelog

Todas as mudanças relevantes deste projeto são documentadas aqui.

O formato segue [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e o projeto adota [Versionamento Semântico](https://semver.org/lang/pt-BR/).
Cada entrada cita a spec de origem (`SPEC-NNNN`); gere-as com `spec_graph.py report SPEC-NNNN --changelog`.

## [Unreleased]
### Added
- Repositório, ferramentas e convenções: solução .NET 10 com Aspire, SPA React/Vite, regras de build e lint, hooks de commit e proteção da `main` (SPEC-0002)
- Harness de testes: unitário, integração com PostgreSQL real, arquitetura (ArchUnitNET), E2E Playwright com axe e cobertura de linhas alteradas (SPEC-0003)
- Pipeline de CI com 7 checks obrigatórios, varreduras de segurança e publicação da imagem no GHCR (SPEC-0004)
- Infraestrutura como código (não provisionada) e deploy por script com migração, health check e rollback, testado em ambiente local prod-like (SPEC-0005)
