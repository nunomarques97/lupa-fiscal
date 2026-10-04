# Answer model selection (v0.2 part 2)

Date: 2026-10-03. Scope: pick a free, openly licensed instruction model that runs in process on the CPU, from .NET on Windows, to write short answers in European Portuguese from the passages hybrid search retrieves, with a citation on every sentence (Sponsor decision 2026-10-03: local and free only).

Outcome: **qwen3-4b is chosen** (Qwen3-4B-Instruct-2507, Apache-2.0, int4 ONNX Runtime GenAI build `robinsmits/qwen3-4b-instruct-2507-onnx-int4` at revision `97d8690faf0cad28279dd10334a67c79f673a57f`; pinned files and SHA-256 at the end). On the final settings it meets every point of the quality bar: raw valid share 1.000 (27/27, bar 0.80), cites an expected ruling in 6 of 8 answers where one was provided (0.750, bar 0.60), usefulness 2.42 and European Portuguese 2.83 (bar 2 of 3), and total p95 37.6 s (bar 60 s) when its process has the CPU. On this shared machine at normal priority its total p95 was 90 to 93 s; see Latency. Phi-4-mini fails on usefulness and Portuguese; EuroLLM-9B fails on citations, refusals and time.

Raw numbers and every transcript: [answer-eval.json](answer-eval.json) (final settings) and [answer-eval-iteration1.json](answer-eval-iteration1.json) (first settings). Both were written by `answer-eval --record`.

## Runtime

ONNX Runtime GenAI, in process, CPU only (technology decision D1 of this run). No service, no network, no account and no key at query time; the model files are read from `data/models/<name>/` after their SHA-256 check.

| Package | Version | Licence | Why |
|---|---|---|---|
| Microsoft.ML.OnnxRuntimeGenAI | 0.17.1 | MIT (LICENSE file in the package) | Generation loop, tokenizer and chat models in ONNX format |
| Microsoft.ML.OnnxRuntimeGenAI.Managed | 0.17.1 (dependency) | MIT | .NET bindings |
| Microsoft.Extensions.AI.Abstractions | 9.8.0 (dependency) | MIT | Pulled in by the bindings, not used directly |
| Microsoft.ML.OnnxRuntime | 1.30.0 (already referenced) | MIT | The GenAI package requires exactly this version, the one the embedder already uses |

All three candidates use the int4 weight-only quantisation of ONNX Runtime (MatMulNBits with accuracy level 4, the fast int8-compute path on CPU), checked in each `model.onnx`. Decoding is greedy. Repeated runs still differed slightly (3 of 12 Qwen answers and 1 of 12 Phi answers changed between two runs of the same build and settings), because multithreaded arithmetic is not bit-identical; the scores below are for the recorded transcripts. The default thread count of the runtime was kept: in a session-1 probe, 6 threads were slower and 16 threads slowed decoding.

Runtime not measured: llama.cpp through LLamaSharp (MIT) with GGUF files. It is the usual alternative and is expected to process prompts faster on this CPU, but it would add a second native runtime and new packages, which D1 only allows when the best model has no usable ONNX build. Every candidate here has one, so it was not installed. See the latency section for why it matters.

## Candidates

| Name in the CLI | Model | Model licence | Build used | Size on disk | Context |
|---|---|---|---|---|---|
| `qwen3-4b` | Qwen3-4B-Instruct-2507 (Alibaba) | Apache-2.0 | `robinsmits/qwen3-4b-instruct-2507-onnx-int4` (community conversion, model card Apache-2.0) | 2.57 GB | 262144 |
| `phi-4-mini` | Phi-4-mini-instruct (Microsoft) | MIT | `microsoft/Phi-4-mini-instruct-onnx`, `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4` (official) | 4.93 GB | 131072 |
| `eurollm-9b` | EuroLLM-9B-Instruct (UTTER project, trained with European Portuguese) | Apache-2.0 | `keisuke-miyako/EuroLLM-9B-Instruct-onnx-int4` (community conversion; its card states no licence of its own, so only the base model's Apache-2.0 applies) | 7.51 GB | 4096 |

All three licences are permissive, compatible with this MIT repository and carry no use restrictions. Excluded before measuring: Llama 3.x and Gemma (custom licences with use restrictions) and Qwen2.5-3B (Qwen research licence). Every file is downloaded from Hugging Face without an account by `model download --model NAME`, through the existing downloader (allowed hosts only, pinned commit, size and SHA-256 checked before the file is kept) and checked again before every load.

## Method

- Sample: 12 questions spread evenly over both frozen eval sets, the same for every model and both iterations: q01, q09, q17, q26, q34, q42 from `eval/questions.json` (CIRS) and t01, t05, t09, t13, t17, t21 from `eval/questions-taxes.json` (CIVA, CIRC, SELO, CIMT). The question files are only read.
- Pipeline, as shipped: hybrid search with no filter (the same search as `search`, one passage per ruling), the top passages labelled `[1]` to `[N]` in rank order inside `<fonte>` delimiters, the question inside `<pergunta>`, then the model, then the citation enforcer in code.
- Command: `dotnet run --project src/LupaFiscal.Cli -- answer-eval --questions eval/questions.json,eval/questions-taxes.json --sample 12 --model NAME --record`, one process per model, so the peak working set is the one of a process holding the embedding model, the 34505 vectors and one answer model, as the API would.
- Metrics: **raw valid share** is the share of the model's sentences, before filtering, that carry only well-formed citations to provided ids; **cites expected** is the share of questions whose answer cites a ruling of the frozen expected list, among the questions where such a ruling was in the prompt; **refusal rate** is the share of answers replaced by the fixed refusal; first token (from the start of generation) and total (search, prompt and generation) are nearest-rank p50 and p95.
- Rubric, scored on the recorded transcripts by reading each shown answer against the question and the cited rulings (by the developer, not by a tax professional):
  - Usefulness: 3 answers the question correctly and directly; 2 relevant and not wrong, but partial, vague or repetitive; 1 does not answer, is refused although the passages answer it, or states something contrary to the cited rulings or the law.
  - European Portuguese: 3 correct and natural European Portuguese; 2 understandable, with slips or awkward or Brazilian constructions; 1 frequent errors, Brazilian forms ("você", "em um", gerunds) or broken words. Scored on the model's raw text, so a refused answer is still scored for language.
- Machine: Intel i5-14400F (16 logical processors), 32 GB RAM, no GPU, Windows 11. It was **not idle**: an Android emulator and, at times, other projects' jobs (including a local LLM server and test runs) shared the CPU. The load changed during the runs, which moves the timings by up to about 2 times; quality numbers do not depend on it.

## Results

Iteration 1: 4 passages of at most 230 tokens, prompt budget 1350 tokens, 200 new tokens, 90 s limit. The prompt's format example was a plausible tax sentence.

| Model | Raw valid share | Cites expected | Refusals | First token p50 / p95 | Total p50 / p95 | Peak working set | Usefulness | Portuguese |
|---|---|---|---|---|---|---|---|---|
| qwen3-4b | 1.000 (33/33) | 0.889 (8/9) | 0/12 | 32.2 / 39.4 s | 45.8 / 63.9 s | 4942 MB | 2.50 | 2.83 |
| phi-4-mini | 0.875 (42/48) | 0.667 (6/9) | 2/12 | 29.7 / 35.6 s | 48.3 / 63.5 s | 5988 MB | 1.25 | 1.50 |
| eurollm-9b | 0.455 (20/44) | 0.778 (7/9) | 4/12 | 55.6 / 62.2 s | 70.2 / 81.5 s | 10058 MB | not scored | not scored |

Two findings changed the pipeline before iteration 2. EuroLLM (5 answers) and Phi (1 answer) copied the prompt's example sentence ("As propinas são dedutíveis à coleta [1]") with a valid id, so the enforcer accepted an unsupported claim: the example is now a placeholder that no real answer says, and the enforcer rejects any sentence containing it. Phi also cited as "(1)" or "(Fonte 2)", now rejected as malformed. The prompt was also made shorter to bring latency down.

Iteration 2 (final settings): 4 passages of at most 180 tokens, prompt budget 1150 tokens, 180 new tokens, 90 s limit.

| Model | Raw valid share | Cites expected | Refusals | First token p50 / p95 | Total p50 / p95 | Peak working set | Usefulness | Portuguese |
|---|---|---|---|---|---|---|---|---|
| qwen3-4b | 1.000 (27/27) | 0.750 (6/8) | 0/12 | 21.6 / 24.2 s | 29.5 / 37.6 s | 4388 MB | 2.42 | 2.83 |
| phi-4-mini | 0.850 (34/40) | 0.778 (7/9) | 2/12 | 42.2 / 48.3 s | 62.9 / 95.7 s | 5625 MB | 1.42 | 1.50 |
| eurollm-9b | 0.308 (12/39) | 0.333 (3/9) | 6/12 | 51.8 / 89.4 s | 70.0 / 94.1 s | 8657 MB | 1.42 | 2.91 |

For comparison, the API with the embedding model and vectors alone holds 739 to 790 MB (`bench`), so the answer model adds about 3.6 GB (Qwen) to 7.9 GB (EuroLLM); all fit in 32 GB.

Reading per model:

- **qwen3-4b** cites correctly by itself (no sentence was removed in any recorded run), stays within the passages, writes short and correct European Portuguese ("aplica-se", "pode beneficiar"), never refused. Its errors are of content: in t21 it draws a conclusion the cited ruling does not draw, and some answers are thin or repeat themselves.
- **phi-4-mini** writes Brazilian or broken Portuguese ("você", "em um", "exento", "abatera"), loops until the token limit, mixes unrelated rulings (IRS Jovem in a dependants question) and often cites as "(Fonte 1)", which the enforcer rejects.
- **eurollm-9b** has the best language but ignores the citation format (it writes "a fonte [2] esclarece" mid-sentence or nothing), so half its answers are refused even when its raw text is right (t21); it is also the slowest, and once hit the 90 s limit after two words.

## Latency

The CPU was shared with other jobs during every run, and each Qwen run of iteration 2 gave a different p95 for the same work. The slowest answer decides the p95 here (nearest rank over 12 questions), so one question that ran while the other jobs held the CPU is enough to exceed 60 s. A load log (CPU total and the eval process's CPU seconds, every 5 s) was kept for the last two runs.

| Run | Conditions | First token p50 / p95 | Total p50 / p95 |
|---|---|---|---|
| qwen3-4b, iteration 1 settings | emulator on about 3 cores | 32.2 / 39.4 s | 45.8 / 63.9 s |
| qwen3-4b, iteration 2, run a | other jobs keeping the CPU at 100 % | 47.9 / 58.4 s | 71.3 / 93.4 s |
| qwen3-4b, iteration 2, run b | started quiet, other jobs back for the last 3 questions | 26.9 / 54.0 s | 38.7 / 90.5 s |
| qwen3-4b, iteration 2, run c | started after 15 s under 40 % load, other jobs back at once: the process got about 3.5 cores for 2 questions, then about 7 | 25.6 / 54.4 s | 38.3 / 91.0 s |
| **qwen3-4b, iteration 2, run d (recorded)** | **process priority AboveNormal; other jobs still running (CPU 72 to 100 %), the process kept about 8 cores** | **21.6 / 24.2 s** | **29.5 / 37.6 s** |
| phi-4-mini, iteration 2, run a | lighter load | 22.2 / 24.0 s | 33.1 / 45.6 s |
| phi-4-mini, iteration 2, run b (recorded) | other jobs at times | 42.2 / 48.3 s | 62.9 / 95.7 s |
| eurollm-9b, iteration 2 (recorded) | other jobs keeping the CPU at 100 % | 51.8 / 89.4 s | 70.0 / 94.1 s |

First token is measured from the start of generation (tokenising and the prompt pass); total also includes the hybrid search. With the CPU for itself, Qwen reads the prompt at about 50 tokens per second and writes about 14 tokens per second, so an answer of 1100 prompt tokens takes about 22 s before the first word and 30 s in all. In run d every answer took 24 to 38 s. When other heavy jobs share the CPU at the same priority, an answer can take 2 to 3 times as long (85 to 93 s in runs a to c).

Conclusion for the bar: Qwen meets "total p95 at most 60 s on the i5-14400F" when generation gets the CPU (run d), and does not when other heavy jobs compete for it at equal priority (runs a to c). For the API this means the host should not run other CPU-heavy work at the same time, and the generation time limit must cover a slow answer. A faster runtime (llama.cpp) or reusing the system prompt's attention cache between questions (ONNX Runtime GenAI `RewindTo`) could cut the time further; neither was tried.

## Rubric per question (iteration 2)

| Question | qwen3-4b U / P | phi-4-mini U / P | eurollm-9b U / P | Note |
|---|---|---|---|---|
| q01 mais-valias, reinvestment in two houses | 3 / 3 | 1 / 1 | 2 / 3 | Qwen: exemption only for the part reinvested in own housing. Phi: says the whole gain is exempt. |
| q09 lower rent rate, company tenant | 3 / 3 | 1 / 2 | 1 / 3 | Qwen: yes, as ruling [3] concluded for a housing contract of over 5 years with a company; the condition of housing use matches ruling [2]. |
| q17 IRS Jovem for a foreigner | 2 / 2 | 1 / 1 | 1 / 2 | Qwen vague; "mesmo se os rendimentos sejam" is a slip. Phi invents marital statuses. |
| q26 loan payoff as a sale expense | 3 / 3 | 1 / 1 | 1 / 3 | Qwen: clear "Não" from the ruling. Phi loops; EuroLLM refuses although [1] answers. |
| q34 adult dependant unable to work | 2 / 2 | 1 / 1 | 1 / 3 | Qwen right but thin. Phi mixes in IRS Jovem age limits. |
| q42 acquisition value after a donation | 2 / 3 | 2 / 2 | 1 / 3 | Qwen and Phi give the ruling's answer (exempt donation: the matrix value of the two years before, not the stamp-duty value); Qwen adds a muddled "market value in the deed". EuroLLM's raw text invents an amount. |
| t01 IVA on psychotherapy | 3 / 3 | 2 / 1 | 2 / 3 | Qwen: exempt under article 9 of the CIVA, clinical psychology. |
| t05 sailing lessons, small turnover | 2 / 3 | 1 / 1 | 1 / 3 | Qwen right (article 53 regime) but repeats itself. Phi and EuroLLM refuse. |
| t09 health insurance as an IRC cost | 3 / 3 | 1 / 2 | 1 / n/a | EuroLLM hit the 90 s limit after two words. |
| t13 rents of an exempt association | 2 / 3 | 1 / 2 | 2 / 3 | Expected ruling not among the passages; answers stay generic. |
| t17 stamp duty on raffle vouchers | 3 / 3 | 3 / 2 | 3 / 3 | All give item 11.2.2 at 45 % from the expected ruling. |
| t21 local lodging, property record | 1 / 3 | 2 / 2 | 1 / 3 | Qwen says local lodging implies a new classification; the cited ruling says it depends on the municipality's licensing. Phi: "not automatically", closer. |
| **Mean** | **2.42 / 2.83** | **1.42 / 1.50** | **1.42 / 2.91** | EuroLLM language mean over 11 (t09 has no text). |

## Transcripts of qwen3-4b (iteration 2)

The shown text, after enforcement, with the cited rulings. `*` marks a ruling of the frozen expected list.

**q01** Vendi o apartamento onde morava e quero usar o dinheiro para comprar uma casa nova para viver e outra para pôr a arrendar. Fico isento de mais-valias sobre o valor todo?

> No caso de reinvestir o lucro da venda de um imóvel em outro destinado a habitação própria, pode haver isenção de mais-valias, conforme previsto no nº5 do artigo 10º do Código do IRS. [1] A isenção aplica-se apenas ao reinvestimento em imóvel afetado à habitação própria, não em casos de arrendamento ou outras finalidades. [1]

Passages: [1] 26895 (CIRS, art. 10); [2] 29065 (CIRS, art. 10) not cited; [3] 28882 (CIRS, art. 10) not cited. 2 of 2 model sentences valid; 1107 prompt tokens, 95 generated, first token 23.1 s, total 29.9 s.

**q09** Arrendei o meu apartamento a uma empresa que o usa para alojar um funcionário. Posso ter a taxa mais baixa dos contratos de arrendamento para habitação de longa duração?

> Pode beneficiar da redução da taxa de IRS prevista no nº 3 do artigo 72º do Código do IRS, desde que o contrato de arrendamento tenha duração superior a 5 anos e o inquilino seja uma pessoa coletiva, como uma empresa, que aloja um funcionário no imóvel. [3] A redução de taxa de IRS aplicável a contratos de arrendamento de longa duração para habitação pode ser concedida quando o contrato é celebrado com uma empresa, desde que o uso seja para fins habitacionais. [2][3]

Passages: [1] 25838 (CIRS, art. 72) not cited; [2] 26047 (CIRS, art. 72)*; [3] 27569 (CIRS, art. 72). 2 of 2 model sentences valid; 1049 prompt tokens, 132 generated, first token 24.2 s, total 37.6 s.

**q17** Sou estrangeiro, estudei no meu país e vim trabalhar para Portugal. Também tenho direito ao IRS Jovem?

> Se estiver residente em Portugal e tiver começado a trabalhar após o período de estudo, pode ter direito ao IRS Jovem, desde que o período de 10 anos para a obtenção do benefício seja contado a partir do início da atividade profissional em Portugal. [1] O regime do IRS Jovem pode ser aplicado mesmo se os rendimentos sejam obtidos no estrangeiro, desde que o contribuinte esteja residente em Portugal e tenha cumprido os requisitos de início da atividade profissional. [2][4]

Passages: [1] 27746 (CIRS, art. 12-B); [2] 26968 (CIRS, art. 12-B); [3] 28538 (CIRS, art. 12-B)* not cited; [4] 27751 (CIRS, art. 12-B). 2 of 2 model sentences valid; 1148 prompt tokens, 124 generated, first token 24.2 s, total 35.1 s.

**q26** Vendi uma casa e tive de pagar ao banco para liquidar o empréstimo e cancelar a hipoteca. Posso abater esses encargos ao ganho da venda?

> Não, o valor despendido com a liquidação do empréstimo, com vista ao distrate da hipoteca, não é fiscalmente aceite como despesa dedutível do ganho da venda. [1]

Passages: [1] 23518 (CIRS, art. 51); [2] 25748 (CIRS, art. 10) not cited; [3] 26804 (CIRS, art. 10) not cited. 1 of 1 model sentences valid; 995 prompt tokens, 48 generated, first token 20.0 s, total 23.6 s.

**q34** O meu filho tem 30 anos e não consegue trabalhar por causa de uma doença. Posso continuar a pô-lo como dependente?

> Pode-se considerar o filho como dependente se for inapto para o trabalho, conforme o caso de filhos maiores com incapacidade para manter uma atividade laboral. [2] A possibilidade de incluir um filho maior inapto para o trabalho como dependente é reconhecida em casos de síndrome de Down com grau de incapacidade de 80%. [1]

Passages: [1] 27691 (CIRS, art. 13)*; [2] 26756 (CIRS, art. 78-C)*; [3] 27688 (CIRS, art. 12-B) not cited; [4] 24493 (CIRC, art. 43) not cited. 2 of 2 model sentences valid; 1094 prompt tokens, 86 generated, first token 21.6 s, total 27.5 s.

**q42** Recebi um apartamento por doação da minha avó e agora quero vendê-lo. Que valor de compra uso para calcular a mais-valia?

> Se o apartamento foi recebido por doação da avó, o valor de aquisição a considerar para a mais-valia é o valor vigente nos dois anos anteriores à data da doação, conforme previsto no artigo 45 do CIRS. [2] Esse valor é determinado em função do valor de mercado reconhecido na escritura de doação, e não o valor do imposto do selo. [3]

Passages: [1] 29065 (CIRS, art. 10) not cited; [2] 28615 (CIRS, art. 45)*; [3] 23932 (CIRS, art. 45)*. 2 of 2 model sentences valid; 1062 prompt tokens, 97 generated, first token 21.0 s, total 27.8 s.

**t01** Sou psicóloga clínica e dou consultas de psicoterapia no meu consultório. Tenho de cobrar IVA aos pacientes?

> Os serviços de psicoterapia prestados na vertente da psicologia clínica, com finalidade de cuidados de saúde, estão isentos de IVA, conforme previsto no artigo 9.º, alínea 1) do CIVA. [2] A atividade de psicologia clínica, quando exercida com finalidade de cura de doenças ou anomalias de saúde, enquadra-se na isenção prevista no artigo 9.º, alínea 1) do CIVA. [3]

Passages: [1] 2083 (CIVA, art. 9)* not cited; [2] 27623 (CIVA, art. 9); [3] 13593 (CIVA, art. 53). 2 of 2 model sentences valid; 1099 prompt tokens, 119 generated, first token 21.6 s, total 29.4 s.

**t05** Vou começar a dar aulas de vela a um clube como trabalhador independente e conto faturar pouco por ano. Posso ficar sem cobrar IVA?

> Pode ficar sem cobrar IVA, pois a prestação de serviços de ensino desportivo a um clube desportivo enquadra-se no regime especial de isenção previsto no artigo 53.º do CIVA. [1] A isenção aplica-se a atividades de ensino desportivo e recreativo, como a de professor de vela, desde 2007. [1] A isenção está prevista no regime especial de isenção do IVA, aplicável a atividades de ensino desportivo. [1]

Passages: [1] 12468 (CIVA, art. 53)*; [2] 7641 (CIVA, art. 2) not cited; [3] 26934 (CIVA, art. 18) not cited. 3 of 3 model sentences valid; 1064 prompt tokens, 123 generated, first token 21.0 s, total 28.8 s.

**t09** A minha empresa quer pagar um seguro de saúde a todos os trabalhadores efetivos. Esse custo conta como gasto no IRC?

> O seguro de saúde atribuído a todos os trabalhadores efetivos pode ser considerado dedutível no IRC, desde que a atribuição seja generalizada e não baseada em critérios específicos como a antiguidade. [1] A empresa pode considerar o custo como dedutível se o seguro for oferecido a todos os colaboradores permanentes, sem exclusões, conforme o entendimento de que a atribuição deve ser geral. [2] Em caso de exclusão de um colaborador por motivo de saúde, com possibilidade de seguro individual, o benefício pode ser considerado dedutível se a atribuição for geral para os outros. [3]

Passages: [1] 25976 (CIRC, art. 43)*; [2] 24493 (CIRC, art. 43)*; [3] 20548 (CIRC, art. 43). 3 of 3 model sentences valid; 1036 prompt tokens, 142 generated, first token 20.2 s, total 29.5 s.

**t13** Somos uma associação sem fins lucrativos isenta de IRC e recebemos rendas de um prédio que nos deixaram. Essas rendas também estão isentas?

> As rendas de um prédio recebidas por uma associação sem fins lucrativos não são automaticamente isentas de IRC, pois a isenção prevista no artigo 10.º do Código do IRC depende da observância de requisitos específicos, como a realização dos fins estatutários. [4] Os rendimentos auferidos no âmbito de um protocolo para a realização dos fins estatutários estão isentos de IRC, nos termos do n.º 3 do artigo 54.º do Código do IRC. [2] Os subsídios destinados a financiar os fins estatutários são considerados rendimentos isentos, conforme previsto no artigo 3.º do processo 16336. [3]

Passages: [1] 25027 (CIRC, art. 11) not cited; [2] 21004 (CIRC, art. 54); [3] 16336 (CIRC, art. 10); [4] 514/18 (CIRC, art. 10). 3 of 3 model sentences valid; 1147 prompt tokens, 174 generated, first token 22.9 s, total 34.2 s.

**t17** A minha loja vai sortear vales de compras pelos clientes. Há imposto do selo sobre esses prémios?

> Os vales de compras obtidos em sorteios são considerados prémios em espécie e estão sujeitos a imposto do selo da Verba 11.2.2 da TGIS à taxa de 45%. [1] A taxa de 45% aplica-se aos prémios em espécie, incluindo vales de compras, nos sorteios, conforme previsto na Verba 11.2.2. [4]

Passages: [1] 19706 (SELO, art. 1)*; [2] 13951 (SELO, art. Verba 11.2) not cited; [3] 16359 (SELO, art. Verba 11.2) not cited; [4] 26950 (SELO, art. Verba 11.2.2). 2 of 2 model sentences valid; 1146 prompt tokens, 104 generated, first token 23.3 s, total 31.1 s.

**t21** Passei o meu apartamento para alojamento local. Tenho de mudar a afetação na caderneta predial de habitação para serviços?

> A afetação de um imóvel adquirido para habitação a Alojamento Local pode levar à caducidade do benefício de isenção ou redução de taxas, se o destino for alterado durante seis anos após a aquisição, exceto em casos específicos como venda ou alteração de agregado familiar. [1] A mudança de destino do imóvel de habitação para alojamento local implica alteração da classificação do imóvel, conforme previsto no Decreto-lei n.º 128/2014. [2] A afetação a Alojamento Local, mesmo em modalidades como hostel, pode ter consequências no regime tributário e na classificação do imóvel, como previsto no artigo 11 do CIMT. [3]

Passages: [1] 27686 (CIMT, art. 11); [2] 10727 (CIMT, art. 17); [3] 26681 (CIMT, art. 11). 3 of 3 model sentences valid; 1121 prompt tokens, 178 generated, first token 23.3 s, total 35.6 s.

## What strict citations do and do not guarantee

By construction and by test, every shown sentence cites only passages that were in the prompt, and an answer with no such sentence is the fixed refusal `Não encontrei nas informações vinculativas pesquisadas uma resposta suportada por fontes.` (the goal's text was cut after "(Nao"; this wording lives in one constant, `CitationEnforcer.RefusalText`). A citation proves where a sentence claims to come from, not that the sentence says what the passage says: in t21 Qwen cites a ruling for a conclusion the ruling does not draw. The UI must keep presenting answers as a reading aid next to the passages, with the reminder to read the official ruling.

## Pinned files of the candidates

The same values are in `src/LupaFiscal.Core/Answering/AnswerModel.cs`. Large files are checked against their Git LFS object id (their SHA-256); small files were hashed after download at the pinned revision.

`robinsmits/qwen3-4b-instruct-2507-onnx-int4` at revision `97d8690faf0cad28279dd10334a67c79f673a57f`, stored in `data/models/qwen3-4b-instruct-2507-int4/`:

| File | Bytes | SHA-256 |
|---|---|---|
| `genai_config.json` | 1,572 | `27a08d97068a3a4b7ebf1c9f35b8e4410e188f1510c5c120db9eec8c1045c26f` |
| `model.onnx` | 424,250 | `7d8cde77a77dc9743be3050a2a7553d70f6488028fb7d94c0524e6869de656c0` |
| `model.onnx.data` | 2,551,480,320 | `0562b2ddc15a9b812496bb709f72402c44fc8fbf2e8bc9f8f8ec25864a1411be` |
| `tokenizer.json` | 11,422,654 | `aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4` |
| `tokenizer_config.json` | 5,644 | `e1ff43043fd7fbe07a7656d301d3c59352984be693c69c4becd4b670f52a374b` |
| `special_tokens_map.json` | 644 | `57255613bbe23c9497211ca68561ff429a51e871dbaf5a59998fa4c8f7fe168a` |
| `added_tokens.json` | 735 | `79f6ec6fcc423d3a82bfac8b9033b1daac7b7bce06a5f5f441637b480cc605de` |
| `vocab.json` | 2,776,833 | `ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910` |
| `merges.txt` | 1,671,853 | `8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5` |
| `chat_template.jinja` | 2,690 | `0ab115e84509e86db2bb05a63edf63778ae7fe624972cdfa982b1e59053d6710` |

`microsoft/Phi-4-mini-instruct-onnx` at revision `fc04c8f93df696602fd9f300a30d1bf2e3081347`, stored in `data/models/phi-4-mini-instruct-int4/`:

| File | Bytes | SHA-256 |
|---|---|---|
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/genai_config.json` | 1,520 | `0fcfa1e663f2bc867f8dc62fae65dd0924f0a4d68b43d1234df742dd19171470` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx` | 52,118,230 | `701aa5d185b6a782bc27104a990dd5b634fa507840b7c42f7ee6f1fb812d0b83` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx.data` | 4,856,573,952 | `cb0267fa60befa1a4ade8c98b6d32a3d67f51abbd307c7f793f132e8d9092131` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/tokenizer.json` | 15,524,095 | `382cc235b56c725945e149cc25f191da667c836655efd0857b004320e90e91ea` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/tokenizer_config.json` | 2,960 | `c565326a315fbe62cda093a59d298828c8f3f823122661325f41f3ba577a7dec` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/special_tokens_map.json` | 587 | `aff38493227d813e29fcf8406e8e90062f1f031aa47d589325e9c31d89ac7cc3` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/added_tokens.json` | 249 | `d4f2aceb0f20b71dd1f4bcc7e052e4412946bf281840b8f83d39f259571af486` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/vocab.json` | 3,910,310 | `6cb65a857824fa6615bb1782d95d882617a8bbce1da0317118586b36f39e98bd` |
| `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/merges.txt` | 2,418,348 | `856ce61180bb689282eed6b3a6838bb1f438399be23aefe9d20eb379791fb4ad` |

`keisuke-miyako/EuroLLM-9B-Instruct-onnx-int4` at revision `453de15dd26e3e3cd5a211db2635bc39902a5ad4`, stored in `data/models/eurollm-9b-instruct-int4/`:

| File | Bytes | SHA-256 |
|---|---|---|
| `genai_config.json` | 1,458 | `5086a4aacc580475659c9f9e3624ddb0cdbfab4dce0bd95e478b1d1b59ba643e` |
| `model.onnx` | 313,327 | `02b88d4641fa940d78f8a3000b2c5ae7f259269bd9747a81f8e04675462d8eda` |
| `model.onnx.data` | 7,492,993,024 | `25efda1238c12e353653814a38f98e172e258f4f00bfbb35b00c0742a2705b51` |
| `tokenizer.json` | 15,782,902 | `e54b0a797c9ead3c0488bfbf18ae5558d5361ea9732ad263d173b02380910f73` |
| `tokenizer_config.json` | 46,980 | `98e2fa08b1559c84d3e63759539c2b75edf4318d92c3e3aa795395e8a7ef27c1` |
| `special_tokens_map.json` | 557 | `5a1e50e0e488ce4306a390682135546eeb6a91039212d85520a4db3e650b5c4f` |
| `tokenizer.model` | 2,408,875 | `42957aec88804838805b335da1bbcce9d116024a20c6c4b56757648b9e348254` |
| `chat_template.jinja` | 295 | `9850c24e4c4adc21d258566d1b1f0c1855c44256287143956631e892a38fc4de` |
