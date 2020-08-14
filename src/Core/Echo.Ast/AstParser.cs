using System;
using System.Collections.Generic;
using System.Linq;
using Echo.Ast.Factories;
using Echo.Ast.Helpers;
using Echo.ControlFlow;
using Echo.ControlFlow.Blocks;
using Echo.ControlFlow.Regions;
using Echo.ControlFlow.Serialization.Blocks;
using Echo.Core.Code;
using Echo.DataFlow;

namespace Echo.Ast
{
    /// <summary>
    /// Transforms a <see cref="ControlFlowGraph{TInstruction}"/> and a <see cref="DataFlowGraph{TContents}"/> into an Ast
    /// </summary>
    public sealed class AstParser<TInstruction>
    {
        private readonly AstParserContext<TInstruction> _context;

        private long _id = -1;
        private long _varCount;
        private long _phiVarCount;
        
        /// <summary>
        /// Creates a new Ast parser with the given <see cref="ControlFlowGraph{TInstruction}"/>
        /// </summary>
        /// <param name="controlFlowGraph">The <see cref="ControlFlowGraph{TInstruction}"/> to parse</param>
        /// <param name="dataFlowGraph">The <see cref="DataFlowGraph{TContents}"/> to parse</param>
        public AstParser(ControlFlowGraph<TInstruction> controlFlowGraph, DataFlowGraph<TInstruction> dataFlowGraph)
        {
            var isa = new AstInstructionSetArchitectureDecorator<TInstruction>(controlFlowGraph.Architecture);
            _context = new AstParserContext<TInstruction>(controlFlowGraph, dataFlowGraph, isa);
        }

        private IInstructionSetArchitecture<TInstruction> Architecture => _context.Architecture;
        private IInstructionSetArchitecture<StatementBase<TInstruction>> AstArchitecture => _context.AstArchitecture;
        private ControlFlowGraph<TInstruction> ControlFlowGraph => _context.ControlFlowGraph;
        private DataFlowGraph<TInstruction> DataFlowGraph => _context.DataFlowGraph;
        
        /// <summary>
        /// Parses the given <see cref="ControlFlowGraph{TInstruction}"/>
        /// </summary>
        /// <returns>A <see cref="ControlFlowGraph{TInstruction}"/> representing the Ast</returns>
        public ControlFlowGraph<StatementBase<TInstruction>> Parse()
        {
            var newGraph = new ControlFlowGraph<StatementBase<TInstruction>>(AstArchitecture);
            var blockBuilder = new BlockBuilder<TInstruction>();
            var rootScope = blockBuilder.ConstructBlocks(ControlFlowGraph);

            // Transform and add regions.
            foreach (var originalRegion in ControlFlowGraph.Regions)
            {
                var newRegion = TransformRegion(originalRegion);
                newGraph.Regions.Add(newRegion);
            }

            // Transform and add nodes.
            foreach (var originalBlock in rootScope.GetAllBlocks())
            {
                var originalNode = ControlFlowGraph.Nodes[originalBlock.Offset];
                var transformedBlock = TransformBlock(originalBlock);
                var newNode = new ControlFlowNode<StatementBase<TInstruction>>(originalBlock.Offset, transformedBlock);
                newGraph.Nodes.Add(newNode);
                
                // Move node to newly created region.
                if (originalNode.ParentRegion is BasicControlFlowRegion<TInstruction> basicRegion)
                    newNode.MoveToRegion(_context.RegionsMapping[basicRegion]);
            }

            // Clone edges.
            foreach (var originalEdge in ControlFlowGraph.GetEdges())
            {
                var newOrigin = newGraph.Nodes[originalEdge.Origin.Offset];
                var newTarget = newGraph.Nodes[originalEdge.Target.Offset];
                newOrigin.ConnectWith(newTarget, originalEdge.Type);
            }
            
            // Fix entry point.
            newGraph.Entrypoint = newGraph.Nodes[_context.ControlFlowGraph.Entrypoint.Offset];

            return newGraph;
        }

        private ControlFlowRegion<StatementBase<TInstruction>> TransformRegion(IControlFlowRegion<TInstruction> region)
        {
            switch (region)
            {
                case BasicControlFlowRegion<TInstruction> basicRegion:
                    // Create new basic region.
                    var newBasicRegion = new BasicControlFlowRegion<StatementBase<TInstruction>>();
                    TransformSubRegions(basicRegion, newBasicRegion);

                    // Register basic region pair.
                    _context.RegionsMapping[basicRegion] = newBasicRegion;

                    return newBasicRegion;

                case ExceptionHandlerRegion<TInstruction> ehRegion:
                    var newEhRegion = new ExceptionHandlerRegion<StatementBase<TInstruction>>();

                    // ProtectedRegion is read-only, so instead we just transform all sub regions and add it to the
                    // existing protected region.
                    TransformSubRegions(ehRegion.ProtectedRegion, newEhRegion.ProtectedRegion);
                    _context.RegionsMapping[ehRegion.ProtectedRegion] = newEhRegion.ProtectedRegion;

                    // Add handler regions.
                    foreach (var subRegion in ehRegion.HandlerRegions)
                        newEhRegion.HandlerRegions.Add(TransformRegion(subRegion));

                    return newEhRegion;

                default:
                    throw new ArgumentOutOfRangeException(nameof(region));
            }

            void TransformSubRegions(
                BasicControlFlowRegion<TInstruction> originalRegion, 
                BasicControlFlowRegion<StatementBase<TInstruction>> newRegion)
            {
                foreach (var subRegion in originalRegion.Regions)
                    newRegion.Regions.Add(TransformRegion(subRegion));
            }
        }

        private BasicBlock<StatementBase<TInstruction>> TransformBlock(BasicBlock<TInstruction> block)
        {
            int phiStatementCount = 0;
            var result = new BasicBlock<StatementBase<TInstruction>>(block.Offset);

            foreach (var instruction in block.Instructions)
            {
                long offset = Architecture.GetOffset(instruction);
                var dataFlowNode = DataFlowGraph.Nodes[offset];
                var stackDependencies = dataFlowNode.StackDependencies;
                var variableDependencies = dataFlowNode.VariableDependencies;
                var targetVariables = VariableFactory.CreateVariableBuffer(
                    stackDependencies.Count + variableDependencies.Count);
                
                for (int i = 0; i < stackDependencies.Count; i++)
                {
                    var sources = stackDependencies[i];
                    if (sources.Count == 1)
                    {
                        var source = sources.First();
                        targetVariables[i] = source.Node is ExternalDataSourceNode<TInstruction> external
                            ? VariableFactory.CreateVariable(external.Name)
                            : _stackSlots[source.Node.Id][source.SlotIndex];
                    }
                    else
                    {
                        var phiVar = CreatePhiSlot();
                        var slots = sources.Select(s =>
                        {
                            var variable = s.Node is ExternalDataSourceNode<TInstruction> external
                                ? VariableFactory.CreateVariable(external.Name)
                                : _stackSlots[s.Node.Id][s.SlotIndex];
                            
                            return new VariableExpression<TInstruction>(_id--, variable);
                        });
                        var phiStatement = new PhiStatement<TInstruction>(_id--, slots.ToArray(), phiVar);
                        
                        result.Instructions.Insert(phiStatementCount++, phiStatement);
                        targetVariables[i] = phiVar;
                    }
                }

                int index = stackDependencies.Count;
                foreach (var pair in variableDependencies)
                {
                    var variable = pair.Key;
                    var dependency = pair.Value;
                    if (dependency.Count <= 1)
                    {
                        if (!_variableVersions.ContainsKey(variable))
                            _variableVersions.Add(variable, 0);
                        
                        if (!_versionedAstVariables.ContainsKey((variable, _variableVersions[variable])))
                            _versionedAstVariables.Add((variable, _variableVersions[variable]), CreateVersionedVariable(variable));

                        targetVariables[index++] = _versionedAstVariables[(variable, _variableVersions[variable])];
                    }
                    else
                    {
                        var sources = AstVariableCollectionFactory<TInstruction>.CollectDependencies(
                            _context, variable, dependency);

                        if (_phiSlots.TryGetValue(sources, out var phiSlot))
                        {
                            targetVariables[index++] = phiSlot;
                        }
                        else
                        {
                            phiSlot = CreatePhiSlot();
                            var phiStatement = new PhiStatement<TInstruction>(
                                _id--,
                                sources.Select(s => new VariableExpression<TInstruction>(_id--, s)).ToArray(),
                                phiSlot);
                            result.Instructions.Insert(phiStatementCount++, phiStatement);
                            _phiSlots[sources] = phiSlot;
                            targetVariables[index++] = phiSlot;
                        }
                    }
                }

                var instructionExpression = new InstructionExpression<TInstruction>(
                    offset,
                    instruction,
                    targetVariables
                        .Select(t => new VariableExpression<TInstruction>(_id--, t))
                        .ToArray()
                );

                int writtenVariablesCount = Architecture.GetWrittenVariablesCount(instruction);
                var writtenVariables = VariableFactory.CreateVariableBuffer(writtenVariablesCount);
                
                if (writtenVariables.Length > 0)
                    Architecture.GetWrittenVariables(instruction, writtenVariables.AsSpan());

                foreach (var writtenVariable in writtenVariables)
                {
                    if (!_instructionToVersionedVariable.TryGetValue(offset, out var dict))
                    {
                        if (!_variableVersions.ContainsKey(writtenVariable))
                            _variableVersions.Add(writtenVariable, 0);
                        else _variableVersions[writtenVariable]++;
                        
                        dict = new Dictionary<IVariable, int>();
                        _instructionToVersionedVariable[offset] = dict;

                        _versionedAstVariables[(writtenVariable, _variableVersions[writtenVariable])] =
                            CreateVersionedVariable(writtenVariable);
                        dict.Add(writtenVariable, _variableVersions[writtenVariable]);
                    }
                    else
                    {
                        if (!dict.ContainsKey(writtenVariable))
                        {
                            if (!_variableVersions.ContainsKey(writtenVariable))
                                _variableVersions.Add(writtenVariable, 0);
                            
                            dict.Add(writtenVariable, _variableVersions[writtenVariable]++);
                        }

                        _versionedAstVariables[(writtenVariable, dict[writtenVariable])] =
                            CreateVersionedVariable(writtenVariable);
                    }
                }

                if (!dataFlowNode.GetDependants().Any() && writtenVariables.Length == 0)
                {
                    result.Instructions.Add(new ExpressionStatement<TInstruction>(_id--, instructionExpression));
                }
                else
                {
                    int stackPushCount = Architecture.GetStackPushCount(instruction);
                    var slots = stackPushCount == 0
                        ? Array.Empty<AstVariable>()
                        : Enumerable.Range(0, stackPushCount)
                            .Select(_ => CreateStackSlot())
                            .ToArray();

                    var combined = VariableFactory.CreateVariableBuffer(writtenVariables.Length + slots.Length);
                    slots.CopyTo(combined, 0);
                    foreach (var writtenVariable in writtenVariables)
                    {
                        int version = _variableVersions[writtenVariable];
                        var key = (writtenVariable, version);
                        if (!_versionedAstVariables.ContainsKey(key))
                            _versionedAstVariables.Add(key, CreateVersionedVariable(writtenVariable));
                        
                        combined[stackPushCount++] = _versionedAstVariables[key];
                    }

                    _stackSlots[offset] = slots;
                    result.Instructions.Add(
                        new AssignmentStatement<TInstruction>(_id--, instructionExpression, combined));
                }
            }

            return result;
        }
        
        private AstVariable CreateStackSlot() => VariableFactory.CreateVariable($"stack_slot_{_varCount++}");

        private AstVariable CreatePhiSlot() => VariableFactory.CreateVariable($"phi_{_phiVarCount++}");

        private AstVariable CreateVersionedVariable(IVariable original) =>
            VariableFactory.CreateVariable(original.Name, _variableVersions[original]);
    }
}